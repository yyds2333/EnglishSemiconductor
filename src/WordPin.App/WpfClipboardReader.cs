using System.Runtime.InteropServices;
using System.Windows;
using WordPin.Application;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace WordPin.App;

public sealed class WpfClipboardReader : IClipboardReader
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(120)
    };

    public async Task<ClipboardReadResult> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < RetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RetryDelays[attempt] > TimeSpan.Zero)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(true);
            }

            try
            {
                if (!WpfClipboard.ContainsText(WpfTextDataFormat.UnicodeText))
                {
                    return new ClipboardReadResult(false, null, ClipboardReadFailure.Empty, "剪贴板没有文本。");
                }

                var text = WpfClipboard.GetText(WpfTextDataFormat.UnicodeText);
                return ClipboardCaptureParser.Parse(text);
            }
            catch (ExternalException) when (attempt < RetryDelays.Length - 1)
            {
                // Another process currently owns the clipboard. Retry briefly.
            }
            catch (ExternalException exception)
            {
                return new ClipboardReadResult(false, null, ClipboardReadFailure.Locked, exception.Message);
            }
        }

        return new ClipboardReadResult(false, null, ClipboardReadFailure.Locked, "剪贴板暂时被其他程序占用。");
    }
}
