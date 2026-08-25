using WordPin.Application;
using WordPin.Domain;

namespace WordPin.Infrastructure.Tests;

public sealed class ClipboardCaptureParserTests
{
    [Fact]
    public void NormalizesExplicitClipboardTextAndDetectsPhrase()
    {
        var result = ClipboardCaptureParser.Parse("  Take\tcare  of ");

        Assert.True(result.Succeeded);
        Assert.Equal("Take care of", result.Text);
        var capture = ClipboardCaptureParser.ToCapture(result);
        Assert.Equal(EntryKind.Phrase, capture.EntryKind);
    }

    [Fact]
    public void RejectsOversizedClipboardTextWithoutTruncatingIt()
    {
        var result = ClipboardCaptureParser.Parse(new string('a', ClipboardCaptureParser.MaxTermCharacters + 1));

        Assert.False(result.Succeeded);
        Assert.Equal(ClipboardReadFailure.TooLong, result.Failure);
        Assert.Null(result.Text);
    }
}
