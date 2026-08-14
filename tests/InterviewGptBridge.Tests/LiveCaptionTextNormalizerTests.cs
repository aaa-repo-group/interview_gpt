using InterviewGptBridge.Services;
using Xunit;

namespace InterviewGptBridge.Tests;

public sealed class LiveCaptionTextNormalizerTests
{
    [Fact]
    public void NormalizeSnapshot_SelectsBestLineFromOverlappingAutomationCandidates()
    {
        var normalized = LiveCaptionTextNormalizer.NormalizeSnapshot(new[]
        {
            "Ready to show live captions in English (United States)",
            "you lead the the elicitation of requirements? And then did you did you do the design or did you work with a solution architect? Could you like talk through your that",
            "So did you, did you lead the the elicitation of requirements? And then did you did you do the design or did you work with a solution architect? Could you like talk through your the",
            "So did you, did you lead the the elicitation of requirements and then did you, did you do the design or did you work with a solution architect? Could you like talk through your that your process there?"
        });

        Assert.Equal(
            "So did you, did you lead the the elicitation of requirements and then did you, did you do the design or did you work with a solution architect? Could you like talk through your that your process there?",
            normalized);
    }

    [Fact]
    public void NormalizeSnapshot_StripsReadyPrefixWhenItIsJoinedWithCaptionText()
    {
        var normalized = LiveCaptionTextNormalizer.NormalizeSnapshot(
            "Ready to show live captions in English (United States) So did you, did you lead requirements?");

        Assert.Equal("So did you, did you lead requirements?", normalized);
    }

    [Fact]
    public void NormalizeSnapshot_SelectsLatestCorrectedRollingWindow()
    {
        var final = "OK. Awesome. But let's let's shift gears. So methodology wise. What like what have you typically used? We we have a hybrid agile, hybrid waterfall agile methodology that we leverage now for this smaller project that's gonna be much more accelerated, but then we're gonna have follow on work for this customer.";
        var normalized = LiveCaptionTextNormalizer.NormalizeSnapshot(new[]
        {
            "OK. Awesome. But let.",
            "OK. Awesome. But let's let's shift gears. So methodology wise. What like what have you typically used? We we have a hybrid, agile hybrid waterfall AG.",
            "OK. Awesome. But let's let's shift gears. So methodology wise. What? Like what have you typically used? We we have a hybrid agile, hybrid waterfall agile methodology",
            final
        });

        Assert.Equal(final, normalized);
    }
}
