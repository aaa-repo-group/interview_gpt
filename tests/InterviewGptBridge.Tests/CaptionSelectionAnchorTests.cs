using InterviewGptBridge.Services;
using Xunit;

namespace InterviewGptBridge.Tests;

public sealed class CaptionSelectionAnchorTests
{
    [Fact]
    public void MapStart_KeepsSameCharacterWhenCaptionOnlyAppends()
    {
        const string oldText = "OK. Awesome.";
        const string newText = "OK. Awesome. But let's shift gears.";

        var mapped = CaptionSelectionAnchor.MapStart(oldText, newText, oldText.Length, oldText);

        Assert.Equal(oldText.Length, mapped);
    }

    [Fact]
    public void MapStart_TracksCorrectedRollingCaptionEndpoint()
    {
        const string oldText = "OK. Awesome. But let.";
        const string newText = "OK. Awesome. But let's let's shift gears.";

        var mapped = CaptionSelectionAnchor.MapStart(oldText, newText, oldText.Length, oldText);

        Assert.Equal("OK. Awesome. But let's".Length, mapped);
    }

    [Fact]
    public void MapStart_TracksSelectedMiddlePointThroughPunctuationCorrection()
    {
        const string oldText = "So methodology wise. What like what have you typically used?";
        const string newText = "So methodology wise. What? Like what have you typically used?";
        var selectedStart = "So methodology wise.".Length;

        var mapped = CaptionSelectionAnchor.MapStart(oldText, newText, selectedStart, oldText[..selectedStart]);

        Assert.Equal("So methodology wise.".Length, mapped);
    }

    [Fact]
    public void MapStart_UsesSuffixWhenEarlierTextWasExpanded()
    {
        const string oldText = "you lead the the elicitation of requirements?";
        const string newText = "So did you, did you lead the the elicitation of requirements and then did you do the design?";

        var mapped = CaptionSelectionAnchor.MapStart(oldText, newText, oldText.Length, oldText);

        Assert.Equal("So did you, did you lead the the elicitation of requirements".Length, mapped);
    }
}
