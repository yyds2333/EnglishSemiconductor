using WordPin.Domain;

namespace WordPin.Application;

public enum ClipboardReadFailure
{
    None = 0,
    Empty = 1,
    Locked = 2,
    TooLong = 3,
    InvalidTerm = 4
}

public sealed record ClipboardReadResult(
    bool Succeeded,
    string? Text,
    ClipboardReadFailure Failure,
    string? Message = null);

public interface IClipboardReader
{
    Task<ClipboardReadResult> ReadTextAsync(CancellationToken cancellationToken = default);
}

public static class ClipboardCaptureParser
{
    public const int MaxClipboardCharacters = 5_000;
    public const int MaxTermCharacters = 200;

    public static ClipboardReadResult Parse(string? clipboardText)
    {
        if (string.IsNullOrWhiteSpace(clipboardText))
        {
            return new ClipboardReadResult(false, null, ClipboardReadFailure.Empty, "剪贴板没有可记录的文本。");
        }

        if (clipboardText.Length > MaxClipboardCharacters)
        {
            return new ClipboardReadResult(false, null, ClipboardReadFailure.TooLong, "剪贴板文本超过 5000 个字符。");
        }

        try
        {
            var term = TermNormalizer.NormalizeDisplay(clipboardText);
            if (term.Length > MaxTermCharacters)
            {
                return new ClipboardReadResult(false, null, ClipboardReadFailure.TooLong, "当前版本只记录不超过 200 个字符的单词或短语。");
            }

            return new ClipboardReadResult(true, term, ClipboardReadFailure.None);
        }
        catch (ArgumentException exception)
        {
            return new ClipboardReadResult(false, null, ClipboardReadFailure.InvalidTerm, exception.Message);
        }
    }

    public static NewWordCapture ToCapture(ClipboardReadResult result)
    {
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Text))
        {
            throw new ArgumentException("A successful clipboard result is required.", nameof(result));
        }

        var entryKind = result.Text.Contains(' ', StringComparison.Ordinal)
            ? EntryKind.Phrase
            : EntryKind.Word;
        return new NewWordCapture(result.Text, EntryKind: entryKind);
    }
}
