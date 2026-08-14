using InterviewGptBridge.Services;
using Xunit;

namespace InterviewGptBridge.Tests;

public sealed class CaptionHistoryMergerTests
{
    [Fact]
    public void Merge_ReplacesPartialCaptionGrowthInsteadOfAppendingDuplicates()
    {
        var history = CaptionHistoryMerger.Merge(string.Empty, string.Empty, "I'm.", repeatedAfterSilence: false);

        history = CaptionHistoryMerger.Merge(history, "I'm.", "I'm primarily an AI engineer.", repeatedAfterSilence: false);

        Assert.Equal("I'm primarily an AI engineer.", history);
    }

    [Fact]
    public void MergeDetailed_ReplacesOnlyTailWhenPartialCaptionGrows()
    {
        var result = CaptionHistoryMerger.MergeDetailed("I'm.", "I'm.", "I'm primarily an AI engineer.", repeatedAfterSilence: false);

        Assert.Equal("I'm primarily an AI engineer.", result.History);
        Assert.Equal(0, result.ReplaceStart);
        Assert.Equal("I'm.".Length, result.ReplaceLength);
        Assert.Equal("I'm primarily an AI engineer.", result.InsertedText);
    }

    [Fact]
    public void Merge_AppendsOnlyNewWordsFromOverlappingLiveCaptionWindow()
    {
        const string history = "I'm primarily an AI engineer with strong Python and AWS expertise.";
        const string snapshot = "Python and AWS expertise. My recent projects have been centered around Amazon Bedrock.";

        var merged = CaptionHistoryMerger.Merge(history, "I'm primarily an AI engineer with strong Python and AWS expertise.", snapshot, repeatedAfterSilence: false);

        Assert.Equal(
            "I'm primarily an AI engineer with strong Python and AWS expertise. My recent projects have been centered around Amazon Bedrock.",
            merged);
    }

    [Fact]
    public void MergeDetailed_AppendsOnlyNewLiveWordsInsteadOfRewritingHistory()
    {
        const string history = "I'm primarily an AI engineer with strong Python and AWS expertise.";
        const string snapshot = "Python and AWS expertise. My recent projects have been centered around Amazon Bedrock.";

        var result = CaptionHistoryMerger.MergeDetailed(
            history,
            "I'm primarily an AI engineer with strong Python and AWS expertise.",
            snapshot,
            repeatedAfterSilence: false);

        Assert.Equal(history.Length, result.ReplaceStart);
        Assert.Equal(0, result.ReplaceLength);
        Assert.Equal(" My recent projects have been centered around Amazon Bedrock.", result.InsertedText);
    }

    [Fact]
    public void Merge_ReplacesMidSentencePartialWhenNextSnapshotContainsItWithEarlierContext()
    {
        const string partial = "you lead the the elicitation of requirements?";
        const string fuller = "So did you, did you lead the the elicitation of requirements and then did you, did you do the design?";

        var merged = CaptionHistoryMerger.Merge(partial, partial, fuller, repeatedAfterSilence: false);

        Assert.Equal(fuller, merged);
    }

    [Fact]
    public void Merge_ReplacesCorrectedRollingWindowInsteadOfRepeatingBeginning()
    {
        var history = CaptionHistoryMerger.Merge(string.Empty, string.Empty, "OK. Awesome. But let.", repeatedAfterSilence: false);
        var previous = "OK. Awesome. But let.";
        var next = "OK. Awesome. But let's let's shift gears. So methodology wise. What like what have you typically used? We we have a hybrid, agile hybrid waterfall AG.";

        history = CaptionHistoryMerger.Merge(history, previous, next, repeatedAfterSilence: false);

        Assert.Equal(next, history);
    }

    [Fact]
    public void MergeDetailed_ReplacesCorrectedRollingTailOnly()
    {
        const string previous = "OK. Awesome. But let.";
        const string next = "OK. Awesome. But let's let's shift gears. So methodology wise.";

        var result = CaptionHistoryMerger.MergeDetailed(previous, previous, next, repeatedAfterSilence: false);

        Assert.Equal(next, result.History);
        Assert.Equal(0, result.ReplaceStart);
        Assert.Equal(previous.Length, result.ReplaceLength);
        Assert.Equal(next, result.InsertedText);
    }

    [Fact]
    public void Merge_KeepsOnlyLatestCorrectedRollingWindowAcrossSeveralUpdates()
    {
        var snapshots = new[]
        {
            "OK. Awesome. But let.",
            "OK. Awesome. But let's let's shift gears. So methodology wise. What like what have you typically used? We we have a hybrid, agile hybrid waterfall AG.",
            "OK. Awesome. But let's let's shift gears. So methodology wise. What like what have you typically used? We we have a hybrid agile, hybrid waterfall agile methodology that we leverage now for this smaller project that's gonna be much more accelerated, but then we're gonna have follow on work for this customer. The idea is that we are implementing.",
            "OK. Awesome. But let's let's shift gears. So methodology wise. What like what have you typically used? We we have a hybrid agile, hybrid waterfall agile methodology that we leverage now for this smaller project that's gonna be much more accelerated, but then we're gonna have follow on work for this customer. The idea is that we are implementing. We are. We are implementing a small amount of work for them now and then we're going to be transitioning into. Umm, a broader implementation beyond that, so this is why I bring up our methodology."
        };
        var history = string.Empty;
        var previous = string.Empty;

        foreach (var snapshot in snapshots)
        {
            history = CaptionHistoryMerger.Merge(history, previous, snapshot, repeatedAfterSilence: false);
            previous = snapshot;
        }

        Assert.Equal(snapshots[^1], history);
    }

    [Fact]
    public void Merge_AllowsSameSentenceAgainAfterSilence()
    {
        const string history = "I have exposure to Azure concepts.";
        const string snapshot = "I have exposure to Azure concepts.";

        var merged = CaptionHistoryMerger.Merge(history, snapshot, snapshot, repeatedAfterSilence: true);

        Assert.Equal("I have exposure to Azure concepts. I have exposure to Azure concepts.", merged);
    }
}
