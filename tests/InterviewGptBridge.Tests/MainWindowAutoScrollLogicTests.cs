using InterviewGptBridge.Services;
using Xunit;

namespace InterviewGptBridge.Tests;

public sealed class MainWindowAutoScrollLogicTests
{
    [Theory]
    [InlineData("")]
    [InlineData("you")]
    [InlineData("[BLANK_AUDIO]")]
    [InlineData("[BLANK_AUDIO] you")]
    public void IsUsableTranscript_RejectsWhisperSilenceNoise(string transcript)
    {
        Assert.False(MainWindowAutoScrollLogic.IsUsableTranscript(transcript));
    }

    [Fact]
    public void IsUsableTranscript_AcceptsMeaningfulPartialReading()
    {
        Assert.True(MainWindowAutoScrollLogic.IsUsableTranscript(
            "Bedrock Claude OpenSearch production grade RAG systems"));
    }

    [Fact]
    public void HasLikelySpeechEnergy_RejectsSilentMicrophoneBuffer()
    {
        var silence = new byte[16000 * 2 * 2];

        Assert.False(MainWindowAutoScrollLogic.HasLikelySpeechEnergy(silence));
    }

    [Fact]
    public void HasLikelySpeechEnergy_AcceptsAudibleMicrophoneBuffer()
    {
        var pcm = new byte[16000 * 2 * 2];
        for (var sampleIndex = 0; sampleIndex < pcm.Length / 2; sampleIndex++)
        {
            var sample = (short)(sampleIndex % 12 < 6 ? 1800 : -1800);
            var bytes = BitConverter.GetBytes(sample);
            pcm[sampleIndex * 2] = bytes[0];
            pcm[(sampleIndex * 2) + 1] = bytes[1];
        }

        var energy = MainWindowAutoScrollLogic.MeasurePcm16AudioEnergy(pcm);

        Assert.True(MainWindowAutoScrollLogic.HasLikelySpeechEnergy(pcm));
        Assert.True(energy.Rms >= MainWindowAutoScrollLogic.MinimumSpeechRms);
        Assert.True(energy.Peak >= MainWindowAutoScrollLogic.MinimumSpeechPeak);
    }

    [Fact]
    public void CreateChunks_FiltersUiChromeAndShortChunks()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("New chat", 0d),
            ("Python backend development with document ingestion embedding generation and orchestration.", 200d)
        });

        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].Index);
    }

    [Fact]
    public void FindBestChunkMatch_MatchesExactMeaningfulSection()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("Earlier in my career I worked more on the data engineering side, building Python ETL pipelines and SQL optimization.", 100d),
            ("I designed an end-to-end document intelligence platform where documents are ingested chunked embedded and indexed into OpenSearch.", 500d),
            ("I built evaluation harnesses with golden datasets and regression testing before every release.", 900d)
        });

        var match = MainWindowAutoScrollLogic.FindBestChunkMatch(
            "documents are ingested chunked embedded and indexed into open search",
            chunks,
            activeChunkIndex: -1);

        Assert.Equal(1, match.Index);
        Assert.True(match.Score >= MainWindowAutoScrollLogic.RequiredMatchScore);
    }

    [Fact]
    public void FindBestChunkMatch_ToleratesParaphrasedReadingIdea()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("She learned to treat dashboards as stories, not decorations, because every metric described a customer waiting somewhere.", 200d),
            ("The migration plan focused on runbooks, ownership notes, and release checklists for operators.", 500d)
        });

        var match = MainWindowAutoScrollLogic.FindBestChunkMatch(
            "I treat the dashboard like a story because metrics describe customers waiting somewhere",
            chunks,
            activeChunkIndex: -1);

        Assert.Equal(0, match.Index);
        Assert.True(match.Score >= MainWindowAutoScrollLogic.RequiredMatchScore);
    }

    [Fact]
    public void FindBestChunkMatch_ToleratesWhisperTyposAndFragments()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("My recent projects have centered around Amazon Bedrock, Claude, OpenSearch, AWS Step Functions, and production grade RAG systems.", 100d),
            ("Earlier I worked on data engineering, Python ETL pipelines, SQL optimization, document indexing, and NLP pipelines.", 400d)
        });

        var match = MainWindowAutoScrollLogic.FindBestChunkMatch(
            "centered around amazon bedrock cloud open search aw step functions production grade rag system",
            chunks,
            activeChunkIndex: -1);

        Assert.Equal(0, match.Index);
        Assert.True(match.Score >= MainWindowAutoScrollLogic.RequiredMatchScore);
    }

    [Fact]
    public void FindBestChunkMatch_GivesPartialReadingPhraseAStrongSentenceAnchor()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("Dashboards and customer metrics can explain operational health during incidents.", 100d),
            ("React is the component model that divides an application into understandable interface parts for users.", 500d),
            ("Evaluation harnesses protect prompts from regressions before the release reaches production.", 900d)
        });

        var match = MainWindowAutoScrollLogic.FindBestChunkMatch(
            "react component model divides application into understandable interface parts",
            chunks,
            activeChunkIndex: -1);

        Assert.Equal(1, match.Index);
        Assert.True(match.Score >= 0.8);
    }

    [Fact]
    public void FindBestChunkMatch_ChoosesForwardRepeatedSentenceAnchor()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("I have exposure to Azure concepts, but my production experience is Python plus AWS.", 100d),
            ("The next answer explains document intelligence architecture with ingestion and retrieval.", 500d),
            ("I have exposure to Azure concepts, but my production experience is Python plus AWS.", 900d)
        });

        var match = MainWindowAutoScrollLogic.FindBestChunkMatch(
            "exposure to azure concepts production experience python aws",
            chunks,
            activeChunkIndex: 1);

        Assert.Equal(2, match.Index);
        Assert.True(match.Score >= MainWindowAutoScrollLogic.RequiredMatchScore);
    }

    [Fact]
    public void FindBestChunkMatch_DoesNotMoveBackwardsBeforeActiveChunk()
    {
        var chunks = MainWindowAutoScrollLogic.CreateChunks(new[]
        {
            ("First section about Python ETL pipelines and document indexing.", 100d),
            ("Second section about OpenSearch retrieval latency and query strategy tuning.", 400d),
            ("Third section about evaluation harnesses and golden datasets.", 700d)
        });

        var match = MainWindowAutoScrollLogic.FindBestChunkMatch(
            "Python ETL pipelines and document indexing",
            chunks,
            activeChunkIndex: 1);

        Assert.NotEqual(0, match.Index);
    }

    [Fact]
    public void DecideScroll_RejectsUpwardScrollAsAlreadyNear()
    {
        var decision = MainWindowAutoScrollLogic.DecideScroll(
            chunkTop: 120,
            scrollY: 500,
            viewportHeight: 800,
            documentHeight: 3000);

        Assert.True(decision.Accepted);
        Assert.Equal(0, decision.Pixels);
    }

    [Fact]
    public void DecideScroll_RejectsFarTargetInsteadOfRegularlyScrollingDown()
    {
        var decision = MainWindowAutoScrollLogic.DecideScroll(
            chunkTop: 2500,
            scrollY: 0,
            viewportHeight: 800,
            documentHeight: 5000);

        Assert.False(decision.Accepted);
        Assert.Equal(0, decision.Pixels);
        Assert.Equal("target too far from current reading band", decision.Reason);
    }

    [Fact]
    public void DecideScroll_CentersConfidentFarReadingPart()
    {
        var decision = MainWindowAutoScrollLogic.DecideScroll(
            chunkTop: 1792.4,
            scrollY: 307,
            viewportHeight: 781,
            documentHeight: 3200,
            matchScore: 0.368);

        Assert.True(decision.Accepted);
        Assert.True(decision.Pixels > 1000);
        Assert.Equal("center confident far reading part", decision.Reason);
    }

    [Fact]
    public void DecideScroll_DoesNotMoveWhenReadingPartIsAroundCenter()
    {
        var decision = MainWindowAutoScrollLogic.DecideScroll(
            chunkTop: 760,
            scrollY: 300,
            viewportHeight: 800,
            documentHeight: 3000);

        Assert.True(decision.Accepted);
        Assert.Equal(0, decision.Pixels);
        Assert.Equal("already in center reading band", decision.Reason);
    }

    [Fact]
    public void DecideScroll_MovesNearbyLowerReadingPartIntoCenterBand()
    {
        var decision = MainWindowAutoScrollLogic.DecideScroll(
            chunkTop: 1000,
            scrollY: 300,
            viewportHeight: 800,
            documentHeight: 3000);

        Assert.True(decision.Accepted);
        Assert.Equal(332, decision.Pixels);
        Assert.Equal("center reading part", decision.Reason);
    }
}
