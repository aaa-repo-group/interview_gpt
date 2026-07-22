# InterviewGptBridge

Windows 10/11 WPF desktop app that opens ChatGPT in an embedded Chromium WebView2 session and shows a topmost caption overlay that can forward selected caption text into the ChatGPT prompt.

The app optionally supports Sensitive Window Protection for its ChatGPT and caption windows. This uses documented Windows display-affinity APIs only and is not a stealth, monitoring-evasion, or remote-administration blocking feature.

## Features

- Embedded WebView2 browser with a persistent per-user profile under `%LOCALAPPDATA%\InterviewGptBridge`.
- First-run device marker so a new Windows profile/device prompts normal ChatGPT sign-in once.
- Tray icon with Open ChatGPT, Show captions, Settings, Hide windows, and Exit actions.
- Main and overlay windows stay off the taskbar.
- Topmost, resizable caption overlay with copyable text.
- Fast polling of Windows Live Captions through UI Automation.
- Live Captions continue updating while text is selected in the overlay.
- Select text in the overlay and press `Enter` to submit it to the embedded ChatGPT page.
- `Shift+Enter` inserts a newline in the overlay text box.
- Overlay opacity, size, position, and topmost state are saved.
- Optional Sensitive Window Protection for windows that can contain private prompts, credentials, captions, notes, recovery information, API keys, authentication codes, or confidential AI responses.

## Requirements

- Windows 10 version 1809 or newer, or Windows 11.
- .NET 8 SDK to build.
- Microsoft Edge WebView2 Runtime on the target machine. Windows 11 normally has it installed; on Windows 10 install the Evergreen runtime if needed.
- Windows Live Captions must be running for caption capture.
- Sensitive Window Protection requires Windows 10 version 2004, build 19041, or newer. Older Windows versions can run the app, but the app warns that this protection is unsupported.

## Build

From PowerShell:

```powershell
.\build\publish-win-x64.ps1
```

The generated executable is written to:

```text
.\artifacts\publish\win-x64\InterviewGptBridge.exe
```

## Usage

1. Start Windows Live Captions.
2. Run `InterviewGptBridge.exe`.
3. Sign in to ChatGPT in the embedded browser the first time.
4. When the ChatGPT prompt is detected, the overlay opens automatically.
5. Select caption text in the overlay and press `Enter` to send it.
6. Use the tray icon to reopen windows or exit the app. Window close buttons hide to tray.
7. Use tray icon -> Settings to enable or disable Sensitive Window Protection.

## Sensitive Window Protection

When enabled, the app centrally registers sensitive top-level windows with `ISensitiveWindowProtectionService`. For each registered sensitive window, the service applies `SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)` after a valid HWND exists. When protection is disabled, a window is unregistered, or the app exits, the service clears display affinity with `WDA_NONE`.

The app shows a visible protection indicator while the feature is enabled. If Windows does not support `WDA_EXCLUDEFROMCAPTURE`, if a window has no HWND yet, or if Windows rejects the display-affinity request, the app shows a warning instead of claiming protection.

Exact user-facing limitation wording:

```text
This feature reduces capture through supported Windows APIs. It cannot prevent every remote-access, administrative, camera, driver-level, or hardware-based capture method.
```

### Reapplication

The service reapplies protection when:

- A registered window gets `SourceInitialized`.
- A registered window becomes visible again or is activated.
- The user enables or disables Sensitive Window Protection.
- The application receives display-configuration change notifications.
- Windows reports resume/session reconnect events.
- A protected overlay is reopened.
- A registered HWND changes between reapplication attempts.

### Protected Windows

Current protected sensitive top-level windows:

- `MainWindow`: embedded ChatGPT WebView2 host; may show confidential AI responses, prompts, authentication content, private notes, credentials, API keys, and recovery codes.
- `OverlayWindow`: caption/prompt overlay; may show live captions, selected text, private notes, and submitted prompt content.

Current top-level windows intentionally not protected:

- `SettingsWindow`: contains feature settings and warnings, but does not display secrets or confidential AI content.

Sensitive window audit:

- No separate authentication-code dialogs, credential windows, private-note windows, or top-level popup windows currently exist in source.
- WebView2 `NewWindowRequested` is handled so requested popup content is opened in the registered `MainWindow` WebView or blocked instead of creating an unregistered top-level popup.
- WebView2 is hosted only inside `MainWindow`; there is no secondary WebView preview/helper window in this codebase.

Limitations:

- This is best-effort protection for ordinary screenshots, screen recordings, Windows Recall, and supported Windows capture or screen-sharing APIs.
- Unsupported capture methods, older operating systems, hardware capture devices, privileged tools, remote-session implementations, monitoring software, endpoint security tools, and administrator-controlled tooling may still see protected windows.
- The app does not detect, interfere with, bypass, disable, or conceal activity from AnyDesk, remote-session tools, monitoring software, endpoint security tools, administrators, or operating-system controls.
- This feature does not encrypt content, redact text inside the app, clear clipboard data, or prevent someone physically viewing the screen.
- This codebase is WPF/WebView2 on Windows. If an Electron shell is added for Windows or macOS, use Electron's documented `BrowserWindow.setContentProtection(true)` for the Electron window path.

### Exposure Review

- Taskbar thumbnails/window previews: `MainWindow`, `OverlayWindow`, and `SettingsWindow` have `ShowInTaskbar="False"`. No custom taskbar thumbnail or preview provider is implemented.
- Clipboard: the overlay warns when users copy protected caption text because clipboard contents can be captured by other software. The app also reads clipboard text as a fallback when submitting prompts.
- Print/export/screenshot: no application-controlled screenshot, print, image export, or document export feature exists in source. WebView2 default context menus are disabled while Sensitive Window Protection is enabled to reduce app-controlled print/copy entry points.
- Logs: Sensitive Window Protection logs structured status metadata only: event name, window type, purpose, HWND, status, error code, affinity. It does not log window contents, captions, prompts, secrets, or AI responses.
- Crash dumps/temporary files: no custom crash dump writer or temporary sensitive-content export path is implemented. WebView2 profile data remains under `%LOCALAPPDATA%\InterviewGptBridge\WebView2Profile`.

## Tests

The Sensitive Window Protection service has unit tests under `tests/InterviewGptBridge.Tests`. They use fakes to verify OS version gating, `WDA_EXCLUDEFROMCAPTURE`, `WDA_NONE`, missing HWNDs, invalid HWND/native failures, enabling/disabling at runtime, multiple registered windows, reopening windows, handle changes, unsupported OS handling, clearing on unregister, and structured log events.

Run them with:

```powershell
dotnet test .\tests\InterviewGptBridge.Tests\InterviewGptBridge.Tests.csproj
```

Windows desktop integration tests live under `tests/InterviewGptBridge.WindowsIntegrationTests`. They are opt-in because they create a real WPF top-level window and call the real Windows API. Run them on an interactive Windows 10 2004/build 19041+ desktop with:

```powershell
$env:RUN_WINDOWS_INTEGRATION_TESTS = "1"
dotnet test .\tests\InterviewGptBridge.WindowsIntegrationTests\InterviewGptBridge.WindowsIntegrationTests.csproj
```

## Manual Verification

Do not mark a capture method as `Protected` unless it was actually tested on that method and OS build.

| Capture method | Required mode | Result | Notes |
| --- | --- | --- | --- |
| Windows Snipping Tool | Standard screenshot | Inconclusive | Not run in this environment. |
| Print Screen | Standard screenshot | Inconclusive | Not run in this environment. |
| Microsoft Teams | Standard screen/window share | Inconclusive | Not run in this environment. |
| Zoom | Standard screen/window share | Inconclusive | Not run in this environment. |
| OBS | Standard display/window capture | Inconclusive | Not run in this environment. |
| Windows Recall | Available Recall capture path | Inconclusive | Not run in this environment. |

Suggested manual steps:

1. Use Windows 10 version 2004/build 19041 or newer.
2. Enable Sensitive Window Protection in tray icon -> Settings.
3. Confirm `MainWindow` and `OverlayWindow` show a persistent protection indicator.
4. Confirm Settings shows no unsupported-OS warning.
5. Test each capture method above and record only observed results: `Protected`, `Visible`, `Unsupported`, or `Inconclusive`.
6. Disable Sensitive Window Protection and confirm captures return to normal visibility for this app.

## Notes

The ChatGPT web UI can change. The submit bridge uses multiple prompt selectors and falls back from send-button click to an Enter key event, but a future ChatGPT DOM change may require updating `ChatGptDomBridge`.
