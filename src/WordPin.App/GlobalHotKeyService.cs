using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;

namespace WordPin.App;

public sealed class GlobalHotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private readonly Action callback;
    private readonly int hotKeyId;
    private HwndSource? source;
    private bool registered;

    public GlobalHotKeyService(Window window, int hotKeyId, Action callback)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.hotKeyId = hotKeyId;
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
        window.SourceInitialized += Window_SourceInitialized;
        window.Closed += Window_Closed;
    }

    public static uint ControlShift => ModControl | ModShift;

    public bool TryRegister(Key key, out int errorCode)
    {
        if (source is null)
        {
            throw new InvalidOperationException("The window source has not been initialized.");
        }

        if (registered)
        {
            errorCode = 0;
            return true;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        registered = RegisterHotKey(source.Handle, hotKeyId, ControlShift, (uint)virtualKey);
        errorCode = registered ? 0 : Marshal.GetLastWin32Error();
        return registered;
    }

    public void Dispose()
    {
        if (registered && source is not null)
        {
            UnregisterHotKey(source.Handle, hotKeyId);
            registered = false;
        }

        if (source is not null)
        {
            source.RemoveHook(WndProc);
            source = null;
        }

        GC.SuppressFinalize(this);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        source = (HwndSource)PresentationSource.FromVisual(window)!;
        source.AddHook(WndProc);
    }

    private void Window_Closed(object? sender, EventArgs e) => Dispose();

    private IntPtr WndProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == hotKeyId)
        {
            callback();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
