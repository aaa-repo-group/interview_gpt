using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using NAudio.Utils;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using Forms = System.Windows.Forms;

namespace InterviewGptBridge.Services;

public sealed class MainWindowAutoScrollReader : IDisposable
{
    private const long MinimumBaseEnglishModelBytes = 130_000_000;
    private const int SampleRate = 16000;
    private const int BytesPerSample = 2;
    private const int Channels = 1;
    private const int MinimumBufferedAudioBytes = SampleRate * BytesPerSample * Channels * 2;
    private const int MaximumBufferedAudioBytes = SampleRate * BytesPerSample * Channels * 14;
    private const int TranscriptAudioOverlapBytes = 0;
    private const string LogFileName = "main-autoscroll.log";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebView2 _browser;
    private readonly Action<string> _statusReporter;
    private readonly Forms.Timer _contentRefreshTimer;
    private readonly Forms.Timer _transcriptionTimer;
    private readonly List<byte> _audioBuffer = [];
    private readonly object _audioLock = new();
    private readonly Queue<string> _pendingCommittedSpeech = [];
    private readonly List<AutoScrollChunk> _chunks = [];
    private WaveInEvent? _waveIn;
    private WhisperFactory? _whisperFactory;
    private bool _disposed;
    private bool _started;
    private bool _isTranscribing;
    private bool _isRefreshingContent;
    private bool _isMatchingSpeech;
    private string _contentSignature = string.Empty;
    private int _activeChunkIndex = -1;
    private int _emptyContentRefreshCount;

    public MainWindowAutoScrollReader(WebView2 browser, Action<string> statusReporter)
    {
        _browser = browser;
        _statusReporter = statusReporter;

        _contentRefreshTimer = new Forms.Timer { Interval = 2500 };
        _contentRefreshTimer.Tick += async (_, _) => await RefreshReadableContentAsync();

        _transcriptionTimer = new Forms.Timer { Interval = 2800 };
        _transcriptionTimer.Tick += async (_, _) => await TranscribeBufferedAudioAsync();
    }

    public static string LogPath => Path.Combine(AppPaths.RootDirectory, LogFileName);

    public async Task StartAsync()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        Log("start requested");

        try
        {
            _statusReporter("Loading local Whisper model for main-window auto-scroll...");
            var modelPath = await EnsureWhisperModelAsync();
            Log("model ready: " + modelPath);
            if (_disposed)
            {
                return;
            }

            _whisperFactory = WhisperFactory.FromPath(modelPath);
            StartMicrophoneRecording();

            _contentRefreshTimer.Start();
            _transcriptionTimer.Start();
            await RefreshReadableContentAsync();
            _statusReporter("Main-window auto-scroll is listening locally.");
            Log("listener started");
        }
        catch (Exception ex)
        {
            _statusReporter("Main-window auto-scroll unavailable: " + ex.Message);
            Log("start failed: " + ex);
        }
    }

    private void StartMicrophoneRecording()
    {
        LogAudioInputDevices();
        Exception? lastException = null;
        foreach (var deviceNumber in GetPreferredWaveInDeviceNumbers())
        {
            WaveInEvent? waveIn = null;
            try
            {
                waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(SampleRate, 16, Channels),
                    BufferMilliseconds = 100
                };
                waveIn.DataAvailable += WaveIn_DataAvailable;
                waveIn.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null && !_disposed)
                    {
                        Log("microphone recording stopped with error: " + e.Exception);
                    }
                };
                waveIn.StartRecording();
                _waveIn = waveIn;
                Log("microphone recording started on " + DescribeWaveInDevice(deviceNumber));
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Log("microphone start failed on " + DescribeWaveInDevice(deviceNumber) + ": " + ex.Message);
                waveIn?.Dispose();
            }
        }

        throw new InvalidOperationException(
            "Could not start a microphone recording device." +
            (lastException is null ? string.Empty : " " + lastException.Message),
            lastException);
    }

    private static IEnumerable<int> GetPreferredWaveInDeviceNumbers()
    {
        var yielded = new HashSet<int>();
        var microphoneLikeDevices = new List<int>();
        var otherDevices = new List<int>();
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            var name = SafeGetWaveInProductName(i);
            if (IsLikelyMicrophoneName(name))
            {
                microphoneLikeDevices.Add(i);
            }
            else
            {
                otherDevices.Add(i);
            }
        }

        foreach (var deviceNumber in microphoneLikeDevices.Concat(otherDevices))
        {
            if (yielded.Add(deviceNumber))
            {
                yield return deviceNumber;
            }
        }

        if (yielded.Add(-1))
        {
            yield return -1;
        }
    }

    private static bool IsLikelyMicrophoneName(string name)
    {
        return name.Contains("mic", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("array", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("input", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("headset", StringComparison.OrdinalIgnoreCase);
    }

    private static string SafeGetWaveInProductName(int deviceNumber)
    {
        try
        {
            return WaveIn.GetCapabilities(deviceNumber).ProductName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeWaveInDevice(int deviceNumber)
    {
        return deviceNumber < 0
            ? "Windows default recording mapper"
            : "device " + deviceNumber.ToString(CultureInfo.InvariantCulture) + " (" + SafeGetWaveInProductName(deviceNumber) + ")";
    }

    private static void LogAudioInputDevices()
    {
        Log("audio input devices: " + WaveIn.DeviceCount.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            try
            {
                var capabilities = WaveIn.GetCapabilities(i);
                Log("audio input device " + i.ToString(CultureInfo.InvariantCulture) +
                    ": " + capabilities.ProductName +
                    ", channels=" + capabilities.Channels.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Log("audio input device " + i.ToString(CultureInfo.InvariantCulture) +
                    ": unavailable, " + ex.Message);
            }
        }
    }

    public void RefreshContentSoon()
    {
        if (_disposed)
        {
            return;
        }

        _ = RefreshReadableContentAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _contentRefreshTimer.Stop();
        _transcriptionTimer.Stop();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _whisperFactory?.Dispose();
        _contentRefreshTimer.Dispose();
        _transcriptionTimer.Dispose();
    }

    private async Task<string> EnsureWhisperModelAsync()
    {
        var modelDirectory = Path.Combine(AppPaths.RootDirectory, "Models");
        Directory.CreateDirectory(modelDirectory);

        var modelPath = Path.Combine(modelDirectory, "ggml-base.en.bin");
        if (File.Exists(modelPath) && new FileInfo(modelPath).Length >= MinimumBaseEnglishModelBytes)
        {
            Log("using existing model");
            return modelPath;
        }

        if (File.Exists(modelPath))
        {
            File.Delete(modelPath);
        }

        var tempModelPath = modelPath + ".download";
        if (File.Exists(tempModelPath))
        {
            File.Delete(tempModelPath);
        }

        _statusReporter("Downloading Whisper model for main-window auto-scroll...");
        Log("downloading model");
        await using (var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn))
        await using (var fileWriter = File.OpenWrite(tempModelPath))
        {
            await modelStream.CopyToAsync(fileWriter);
        }

        var downloadedSize = new FileInfo(tempModelPath).Length;
        if (downloadedSize < MinimumBaseEnglishModelBytes)
        {
            File.Delete(tempModelPath);
            throw new InvalidOperationException(
                "Whisper model download was incomplete. Restart the app to try again.");
        }

        File.Move(tempModelPath, modelPath);
        Log("downloaded model bytes: " + downloadedSize.ToString(CultureInfo.InvariantCulture));
        return modelPath;
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        lock (_audioLock)
        {
            for (var i = 0; i < e.BytesRecorded; i++)
            {
                _audioBuffer.Add(e.Buffer[i]);
            }

            if (_audioBuffer.Count > MaximumBufferedAudioBytes)
            {
                _audioBuffer.RemoveRange(0, _audioBuffer.Count - MaximumBufferedAudioBytes);
            }
        }
    }

    private async Task TranscribeBufferedAudioAsync()
    {
        if (_disposed || _isTranscribing || _whisperFactory is null)
        {
            return;
        }

        byte[] pcmBytes;
        lock (_audioLock)
        {
            if (_audioBuffer.Count < MinimumBufferedAudioBytes)
            {
                return;
            }

            pcmBytes = _audioBuffer.ToArray();
            var keepBytes = Math.Min(TranscriptAudioOverlapBytes, _audioBuffer.Count);
            if (keepBytes <= 0)
            {
                _audioBuffer.Clear();
            }
            else
            {
                _audioBuffer.RemoveRange(0, _audioBuffer.Count - keepBytes);
            }
        }

        _isTranscribing = true;
        var energy = MainWindowAutoScrollLogic.MeasurePcm16AudioEnergy(pcmBytes);
        Log("transcribing microphone audio bytes=" +
            pcmBytes.Length.ToString(CultureInfo.InvariantCulture) +
            " rms=" + energy.Rms.ToString("0.0", CultureInfo.InvariantCulture) +
            " peak=" + energy.Peak.ToString(CultureInfo.InvariantCulture));

        try
        {
            var text = await Task.Run(async () => await TranscribePcmAsync(pcmBytes));
            if (MainWindowAutoScrollLogic.IsUsableTranscript(text) && !_disposed)
            {
                Log("transcript: " + TrimForLog(text));
                await FollowRecognizedTextAsync(text);
            }
            else
            {
                Log("ignored empty/noise transcript: " + TrimForLog(text));
            }
        }
        catch (Exception ex)
        {
            _statusReporter("Main-window auto-scroll transcription failed: " + ex.Message);
            Log("transcription failed: " + ex);
        }
        finally
        {
            _isTranscribing = false;
        }
    }

    private async Task<string> TranscribePcmAsync(byte[] pcmBytes)
    {
        using var wavStream = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wavStream), new WaveFormat(SampleRate, 16, Channels)))
        {
            writer.Write(pcmBytes, 0, pcmBytes.Length);
        }

        wavStream.Position = 0;

        using var processor = _whisperFactory!.CreateBuilder()
            .WithLanguage("en")
            .Build();

        var parts = new List<string>();
        await foreach (var result in processor.ProcessAsync(wavStream))
        {
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                parts.Add(result.Text.Trim());
            }
        }

        return string.Join(' ', parts);
    }

    private async Task RefreshReadableContentAsync()
    {
        if (_disposed || _isRefreshingContent || _browser.CoreWebView2 is null)
        {
            return;
        }

        _isRefreshingContent = true;
        try
        {
            var result = await _browser.CoreWebView2.ExecuteScriptAsync(ExtractReadableContentScript);
            var snapshot = JsonSerializer.Deserialize<ReadableContentSnapshot>(result, JsonOptions);
            if (snapshot?.Chunks is null || snapshot.Chunks.Count == 0)
            {
                Log("content refresh found no chunks");
                return;
            }

            var signature = snapshot.Signature ?? string.Empty;
            var signatureChanged = !string.Equals(signature, _contentSignature, StringComparison.Ordinal);
            _contentSignature = signature;

            var nextChunks = new List<AutoScrollChunk>();
            for (var i = 0; i < snapshot.Chunks.Count; i++)
            {
                var chunk = snapshot.Chunks[i];
                if (string.IsNullOrWhiteSpace(chunk.Text))
                {
                    continue;
                }

                var nextChunk = MainWindowAutoScrollLogic.CreateChunk(
                    nextChunks.Count,
                    chunk.Text,
                    Math.Max(0, chunk.Top));
                if (nextChunk.Words.Length < 4)
                {
                    continue;
                }

                nextChunks.Add(nextChunk);
            }

            if (nextChunks.Count == 0)
            {
                _emptyContentRefreshCount++;
                Log("content refresh produced no readable chunks; keeping previous chunks=" +
                    _chunks.Count.ToString(CultureInfo.InvariantCulture) +
                    " emptyCount=" + _emptyContentRefreshCount.ToString(CultureInfo.InvariantCulture));
                if (_emptyContentRefreshCount >= 8 && _chunks.Count > 0)
                {
                    _chunks.Clear();
                    _activeChunkIndex = -1;
                    _pendingCommittedSpeech.Clear();
                    Log("cleared stale readable chunks after repeated empty refreshes");
                }

                return;
            }

            _emptyContentRefreshCount = 0;
            _chunks.Clear();
            _chunks.AddRange(nextChunks);

            if (signatureChanged)
            {
                _activeChunkIndex = -1;
                _pendingCommittedSpeech.Clear();
            }

            Log("content chunks ready: " + _chunks.Count.ToString(CultureInfo.InvariantCulture) +
                ", signatureChanged=" + signatureChanged);
        }
        catch (Exception ex)
        {
            // ChatGPT frequently replaces its DOM during navigation/streaming; the next timer tick will retry.
            Log("content refresh failed: " + ex.Message);
        }
        finally
        {
            _isRefreshingContent = false;
        }
    }

    private async Task FollowRecognizedTextAsync(string text)
    {
        if (_disposed || _isMatchingSpeech)
        {
            return;
        }

        _isMatchingSpeech = true;
        try
        {
            AddCommittedSpeech(text);
            var speechWindow = string.Join(' ', _pendingCommittedSpeech);
            var meaningfulWordCount = MainWindowAutoScrollLogic.NormalizeWords(speechWindow).Count();
            if (meaningfulWordCount < MainWindowAutoScrollLogic.MinimumMeaningfulWords)
            {
                Log("waiting for more speech words=" + meaningfulWordCount.ToString(CultureInfo.InvariantCulture) +
                    ": " + TrimForLog(speechWindow));
                return;
            }

            if (_chunks.Count == 0)
            {
                await RefreshReadableContentAsync();
            }

            var match = MainWindowAutoScrollLogic.FindBestChunkMatch(speechWindow, _chunks, _activeChunkIndex);
            var matchedChunk = match.Index >= 0 && match.Index < _chunks.Count ? _chunks[match.Index] : null;
            Log("match result index=" + match.Index.ToString(CultureInfo.InvariantCulture) +
                " score=" + match.Score.ToString("0.000", CultureInfo.InvariantCulture) +
                (matchedChunk is null
                    ? string.Empty
                    : " chunkTop=" + matchedChunk.Top.ToString("0.0", CultureInfo.InvariantCulture) +
                      " chunk=\"" + TrimForLog(matchedChunk.Text) + "\"") +
                " speech=" + TrimForLog(speechWindow));
            if (match.Index < 0 || match.Score < MainWindowAutoScrollLogic.RequiredMatchScore)
            {
                return;
            }

            if (match.Index < _activeChunkIndex)
            {
                return;
            }

            var scrollResult = await TryScrollChunkDownIntoReadingPositionAsync(_chunks[match.Index], match.Score);
            if (!scrollResult.Accepted)
            {
                Log("scroll rejected for match index=" + match.Index.ToString(CultureInfo.InvariantCulture));
                return;
            }

            _activeChunkIndex = match.Index;
            _pendingCommittedSpeech.Clear();
            Log("accepted match index=" + match.Index.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _isMatchingSpeech = false;
        }
    }

    private async Task<FollowScrollResult> TryScrollChunkDownIntoReadingPositionAsync(AutoScrollChunk chunk, double matchScore)
    {
        var state = await GetScrollStateAsync();
        if (state is null || state.ViewportHeight <= 0)
        {
            Log("scroll rejected: missing scroll state");
            return FollowScrollResult.Rejected;
        }

        var decision = MainWindowAutoScrollLogic.DecideScroll(
            chunk.Top,
            state.ScrollY,
            state.ViewportHeight,
            state.DocumentHeight,
            matchScore);
        if (!decision.Accepted)
        {
            Log("scroll rejected: " + decision.Reason +
                " chunkTop=" + chunk.Top.ToString("0.0", CultureInfo.InvariantCulture) +
                " current=" + state.ScrollY.ToString("0.0", CultureInfo.InvariantCulture) +
                " viewport=" + state.ViewportHeight.ToString("0.0", CultureInfo.InvariantCulture));
            return FollowScrollResult.Rejected;
        }

        if (decision.Pixels <= 0)
        {
            Log("chunk already near reading position; chunkTop=" +
                chunk.Top.ToString("0.0", CultureInfo.InvariantCulture) +
                " current=" + state.ScrollY.ToString("0.0", CultureInfo.InvariantCulture));
            return FollowScrollResult.Anchored;
        }

        await ScrollByAsync(decision.Pixels);
        Log("discrete scroll: " + decision.Pixels.ToString("0.0", CultureInfo.InvariantCulture) +
            " reason=" + decision.Reason +
            " chunkTop=" + chunk.Top.ToString("0.0", CultureInfo.InvariantCulture) +
            " current=" + state.ScrollY.ToString("0.0", CultureInfo.InvariantCulture));
        return FollowScrollResult.Anchored;
    }

    private async Task<ScrollState?> GetScrollStateAsync()
    {
        if (_browser.CoreWebView2 is null)
        {
            return null;
        }

        var result = await _browser.CoreWebView2.ExecuteScriptAsync(GetScrollStateScript);
        return JsonSerializer.Deserialize<ScrollState>(result, JsonOptions);
    }

    private async Task ScrollByAsync(double pixels)
    {
        if (_browser.CoreWebView2 is null || pixels <= 0)
        {
            return;
        }

        var formattedPixels = pixels.ToString("0.###", CultureInfo.InvariantCulture);
        await _browser.CoreWebView2.ExecuteScriptAsync(BuildScrollByScript(formattedPixels));
    }

    private void AddCommittedSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _pendingCommittedSpeech.Enqueue(text);

        while (_pendingCommittedSpeech.Count > MainWindowAutoScrollLogic.MaximumPendingSpeechSegments ||
            MainWindowAutoScrollLogic.NormalizeWords(string.Join(' ', _pendingCommittedSpeech)).Count() > MainWindowAutoScrollLogic.MaximumPendingSpeechWords)
        {
            _pendingCommittedSpeech.Dequeue();
        }
    }

    private static string TrimForLog(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 180 ? normalized : normalized[..177] + "...";
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RootDirectory);
            var line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture) +
                " " + message + Environment.NewLine;
            File.AppendAllText(LogPath, line);
        }
        catch
        {
        }
    }

    private static string BuildScrollByScript(string formattedPixels)
    {
        return """
        (() => {
          const pixels = __PIXELS__;
          const canScroll = (element) => {
            if (!element || element === document.body || element === document.documentElement) return false;
            const style = window.getComputedStyle(element);
            const overflow = `${style.overflowY} ${style.overflow}`;
            return element.scrollHeight > element.clientHeight + 8 && /(auto|scroll)/.test(overflow);
          };
          const findScrollContainer = () => {
            const assistants = Array.from(document.querySelectorAll('[data-message-author-role="assistant"]'));
            const anchors = [
              assistants[assistants.length - 1],
              document.querySelector('#prompt-textarea, [data-testid="prompt-textarea"], textarea, [role="textbox"]'),
              document.querySelector('main')
            ].filter(Boolean);

            const candidates = [];
            const addCandidate = (element) => {
              if (canScroll(element) && !candidates.includes(element)) {
                candidates.push(element);
              }
            };
            for (const anchor of anchors) {
              for (let node = anchor.parentElement; node; node = node.parentElement) {
                addCandidate(node);
              }
            }

            Array.from(document.querySelectorAll('main, main *')).forEach(addCandidate);
            candidates.sort((left, right) =>
              (right.clientHeight - left.clientHeight) ||
              (right.scrollHeight - left.scrollHeight));
            return candidates[0] || window;
          };

          const scroller = findScrollContainer();
          const getScrollTop = () => scroller === window
            ? (window.scrollY || document.documentElement.scrollTop || 0)
            : (scroller.scrollTop || 0);
          const getViewportHeight = () => scroller === window
            ? (window.innerHeight || document.documentElement.clientHeight || 0)
            : (scroller.clientHeight || 0);
          const getMaxScrollTop = () => {
            if (scroller === window) {
              const height = Math.max(
                document.documentElement?.scrollHeight || 0,
                document.body?.scrollHeight || 0,
                document.documentElement?.offsetHeight || 0,
                document.body?.offsetHeight || 0);
              return Math.max(0, height - getViewportHeight());
            }

            return Math.max(0, scroller.scrollHeight - scroller.clientHeight);
          };
          const setScrollTop = (value) => {
            if (scroller === window) {
              window.scrollTo(0, value);
              return;
            }

            scroller.scrollTop = value;
          };
          const start = getScrollTop();
          const target = Math.min(getMaxScrollTop(), start + pixels);
          if (target <= start + 0.5) {
            return true;
          }

          if (window.__interviewGptAutoScrollFrame) {
            cancelAnimationFrame(window.__interviewGptAutoScrollFrame);
          }

          const tick = () => {
            const current = getScrollTop();
            const remaining = target - current;
            if (remaining <= 0.5) {
              setScrollTop(target);
              window.__interviewGptAutoScrollFrame = 0;
              return;
            }

            const speed = remaining > getViewportHeight() * 0.75 ? 18 : 7;
            setScrollTop(current + Math.min(remaining, speed));
            window.__interviewGptAutoScrollFrame = requestAnimationFrame(tick);
          };

          window.__interviewGptAutoScrollFrame = requestAnimationFrame(tick);
          return true;
        })();
        """.Replace("__PIXELS__", formattedPixels, StringComparison.Ordinal);
    }

    private sealed class ReadableContentSnapshot
    {
        public string? Signature { get; set; }
        public List<ReadableContentChunk> Chunks { get; set; } = [];
    }

    private sealed class ReadableContentChunk
    {
        public string Text { get; set; } = string.Empty;
        public double Top { get; set; }
    }

    private sealed class ScrollState
    {
        public double ScrollY { get; set; }
        public double ViewportHeight { get; set; }
        public double DocumentHeight { get; set; }
    }

    private sealed record FollowScrollResult(bool Accepted, bool AnchorMatch)
    {
        public static FollowScrollResult Rejected { get; } = new(false, false);

        public static FollowScrollResult Anchored { get; } = new(true, true);
    }

    private const string GetScrollStateScript =
        """
        (() => {
          const canScroll = (element) => {
            if (!element || element === document.body || element === document.documentElement) return false;
            const style = window.getComputedStyle(element);
            const overflow = `${style.overflowY} ${style.overflow}`;
            return element.scrollHeight > element.clientHeight + 8 && /(auto|scroll)/.test(overflow);
          };
          const findScrollContainer = () => {
            const assistants = Array.from(document.querySelectorAll('[data-message-author-role="assistant"]'));
            const anchors = [
              assistants[assistants.length - 1],
              document.querySelector('#prompt-textarea, [data-testid="prompt-textarea"], textarea, [role="textbox"]'),
              document.querySelector('main')
            ].filter(Boolean);

            const candidates = [];
            const addCandidate = (element) => {
              if (canScroll(element) && !candidates.includes(element)) {
                candidates.push(element);
              }
            };
            for (const anchor of anchors) {
              for (let node = anchor.parentElement; node; node = node.parentElement) {
                addCandidate(node);
              }
            }

            Array.from(document.querySelectorAll('main, main *')).forEach(addCandidate);
            candidates.sort((left, right) =>
              (right.clientHeight - left.clientHeight) ||
              (right.scrollHeight - left.scrollHeight));
            return candidates[0] || window;
          };

          const scroller = findScrollContainer();
          if (scroller === window) {
            return {
              scrollY: window.scrollY || document.documentElement.scrollTop || 0,
              viewportHeight: window.innerHeight || document.documentElement.clientHeight || 0,
              documentHeight: Math.max(
                document.documentElement?.scrollHeight || 0,
                document.body?.scrollHeight || 0,
                document.documentElement?.offsetHeight || 0,
                document.body?.offsetHeight || 0)
            };
          }

          return {
            scrollY: scroller.scrollTop || 0,
            viewportHeight: scroller.clientHeight || 0,
            documentHeight: scroller.scrollHeight || 0
          };
        })();
        """;

    private const string ExtractReadableContentScript =
        """
        (() => {
          const clean = (text) => (text || '').replace(/\s+/g, ' ').trim();
          const canScroll = (element) => {
            if (!element || element === document.body || element === document.documentElement) return false;
            const style = window.getComputedStyle(element);
            const overflow = `${style.overflowY} ${style.overflow}`;
            return element.scrollHeight > element.clientHeight + 8 && /(auto|scroll)/.test(overflow);
          };
          const findScrollContainer = (roots) => {
            const assistants = Array.from(document.querySelectorAll('[data-message-author-role="assistant"]'));
            const anchors = [
              ...roots,
              assistants[assistants.length - 1],
              document.querySelector('#prompt-textarea, [data-testid="prompt-textarea"], textarea, [role="textbox"]'),
              document.querySelector('main')
            ].filter(Boolean);

            const candidates = [];
            const addCandidate = (element) => {
              if (canScroll(element) && !candidates.includes(element)) {
                candidates.push(element);
              }
            };
            for (const anchor of anchors) {
              for (let node = anchor.parentElement; node; node = node.parentElement) {
                addCandidate(node);
              }
            }

            Array.from(document.querySelectorAll('main, main *')).forEach(addCandidate);
            candidates.sort((left, right) =>
              (right.clientHeight - left.clientHeight) ||
              (right.scrollHeight - left.scrollHeight));
            return candidates[0] || window;
          };
          const isVisible = (element) => {
            if (!element) return false;
            const rect = element.getBoundingClientRect();
            const style = window.getComputedStyle(element);
            return rect.width > 24 &&
              rect.height > 4 &&
              style.display !== 'none' &&
              style.visibility !== 'hidden' &&
              style.opacity !== '0';
          };

          const splitChunks = (source) => {
            if (!source) return [];
            const result = [];
            const minimumWords = 5;
            const maximumSentenceWords = 34;
            const longSectionWords = 26;
            const wordRegex = /\S+/g;
            const wordSpans = (start, end) => {
              const words = [];
              wordRegex.lastIndex = Math.max(0, start);
              let wordMatch;
              while ((wordMatch = wordRegex.exec(source)) !== null) {
                if (wordMatch.index >= end) break;
                words.push({
                  text: wordMatch[0],
                  start: wordMatch.index,
                  end: Math.min(end, wordMatch.index + wordMatch[0].length)
                });
              }

              return words;
            };
            const addSpan = (start, end) => {
              let spanStart = Math.max(0, start);
              let spanEnd = Math.min(source.length, end);
              while (spanStart < spanEnd && /\s/.test(source[spanStart])) spanStart++;
              while (spanEnd > spanStart && /\s/.test(source[spanEnd - 1])) spanEnd--;
              if (spanEnd <= spanStart) return;

              const text = clean(source.slice(spanStart, spanEnd));
              if (text.split(/\s+/).filter(Boolean).length >= minimumWords) {
                result.push({ text, start: spanStart, end: spanEnd });
              }
            };
            const addSentence = (start, end) => {
              const words = wordSpans(start, end);
              if (words.length < minimumWords) return;
              if (words.length <= maximumSentenceWords) {
                addSpan(start, end);
                return;
              }

              const sections = [];
              for (let i = 0; i < words.length; i += longSectionWords) {
                const sectionWords = words.slice(i, i + longSectionWords);
                if (sectionWords.length === 0) continue;
                sections.push({
                  start: sectionWords[0].start,
                  end: sectionWords[sectionWords.length - 1].end,
                  words: sectionWords.length
                });
              }

              if (sections.length > 1 && sections[sections.length - 1].words < minimumWords) {
                sections[sections.length - 2].end = sections[sections.length - 1].end;
                sections.pop();
              }

              for (const section of sections) {
                addSpan(section.start, section.end);
              }
            };
            const sentenceRegex = /[^.!?]+(?:[.!?]+|$)/g;
            let match;
            while ((match = sentenceRegex.exec(source)) !== null) {
              addSentence(match.index, match.index + match[0].length);
            }

            if (result.length === 0) {
              addSentence(0, source.length);
            }

            return result;
          };
          const normalizeWithMap = (rawText) => {
            const text = [];
            const map = [];
            for (let i = 0; i < rawText.length; i++) {
              const ch = rawText[i];
              if (/\s/.test(ch)) {
                if (text.length > 0 && text[text.length - 1] !== ' ') {
                  text.push(' ');
                  map.push(i);
                }
                continue;
              }

              text.push(ch);
              map.push(i);
            }

            while (text.length > 0 && text[text.length - 1] === ' ') {
              text.pop();
              map.pop();
            }

            return { text: text.join(''), map };
          };
          const collectTextNodes = (element) => {
            const nodes = [];
            const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
            for (let node = walker.nextNode(); node; node = walker.nextNode()) {
              if ((node.nodeValue || '').trim().length > 0) {
                nodes.push(node);
              }
            }

            return nodes;
          };
          const getDomPoint = (nodes, rawOffset) => {
            let remaining = Math.max(0, rawOffset);
            for (const node of nodes) {
              const length = (node.nodeValue || '').length;
              if (remaining <= length) {
                return { node, offset: remaining };
              }

              remaining -= length;
            }

            const fallbackNode = nodes[nodes.length - 1];
            return fallbackNode
              ? { node: fallbackNode, offset: (fallbackNode.nodeValue || '').length }
              : null;
          };
          const measurePartTop = (nodes, normalized, part, fallbackTop) => {
            if (!part || part.start < 0 || part.end <= part.start || nodes.length === 0 || normalized.map.length === 0) {
              return fallbackTop;
            }

            const startIndex = Math.min(part.start, normalized.map.length - 1);
            const endIndex = Math.min(part.end - 1, normalized.map.length - 1);
            const startPoint = getDomPoint(nodes, normalized.map[startIndex]);
            const endPoint = getDomPoint(nodes, normalized.map[endIndex] + 1);
            if (!startPoint || !endPoint) {
              return fallbackTop;
            }

            try {
              const range = document.createRange();
              range.setStart(startPoint.node, startPoint.offset);
              range.setEnd(endPoint.node, endPoint.offset);
              const rects = Array.from(range.getClientRects())
                .filter((rect) => rect.width > 1 && rect.height > 1);
              const rect = rects[0] || range.getBoundingClientRect();
              range.detach?.();
              if (!rect || rect.height <= 0) {
                return fallbackTop;
              }

              return Math.max(0, scrollerScrollTop + rect.top - scrollerTop);
            } catch {
              return fallbackTop;
            }
          };

          const assistantRoots = Array.from(document.querySelectorAll('[data-message-author-role="assistant"]'))
            .filter((element) => isVisible(element) && clean(element.innerText).length > 40);

          let roots = assistantRoots.slice(-1);
          if (roots.length === 0) {
            roots = Array.from(document.querySelectorAll('main article, main [role="article"], article'))
              .filter((element) => isVisible(element) && clean(element.innerText).length > 80)
              .slice(-1);
          }

          if (roots.length === 0) {
            const main = document.querySelector('main') || document.body;
            roots = main ? [main] : [];
          }

          const scroller = findScrollContainer(roots);
          const scrollerTop = scroller === window ? 0 : scroller.getBoundingClientRect().top;
          const scrollerScrollTop = scroller === window
            ? (window.scrollY || document.documentElement.scrollTop || 0)
            : (scroller.scrollTop || 0);
          const chunks = [];
          for (const root of roots) {
            const blockCandidates = Array.from(root.querySelectorAll('p, li, h1, h2, h3, h4, blockquote, pre'))
              .filter(isVisible);
            const blocks = blockCandidates.length > 0 ? blockCandidates : [root];

            for (const block of blocks) {
              const rawText = block.textContent || block.innerText || '';
              const normalized = normalizeWithMap(rawText);
              const text = normalized.text;
              if (text.length < 18) continue;

              const rect = block.getBoundingClientRect();
              const textNodes = collectTextNodes(block);
              const parts = splitChunks(text);
              for (const part of parts) {
                const ratio = Math.max(0, Math.min(0.98, part.start / Math.max(1, text.length)));
                const fallbackTop = Math.max(0, scrollerScrollTop + rect.top - scrollerTop + (rect.height * ratio));
                chunks.push({
                  text: part.text,
                  top: measurePartTop(textNodes, normalized, part, fallbackTop)
                });
              }
            }
          }

          const signature = clean(roots.map((root) => clean(root.textContent || root.innerText)).join('\n')).slice(-5000);
          return {
            signature,
            chunks: chunks.slice(-180)
          };
        })();
        """;
}
