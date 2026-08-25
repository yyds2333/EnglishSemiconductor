using System.Windows;
using System.Data.Common;
using System.IO;
using System.Windows.Input;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly IClipboardReader clipboardReader;
    private readonly IWordRepository wordRepository;
    private readonly GlobalHotKeyService globalHotKeyService;

    public MainWindow(IClipboardReader clipboardReader, IWordRepository wordRepository)
    {
        this.clipboardReader = clipboardReader ?? throw new ArgumentNullException(nameof(clipboardReader));
        this.wordRepository = wordRepository ?? throw new ArgumentNullException(nameof(wordRepository));
        InitializeComponent();
        globalHotKeyService = new GlobalHotKeyService(this, hotKeyId: 1001, HandleGlobalCapture);
        SourceInitialized += MainWindow_SourceInitialized;
    }

    private async void ReadClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        await ReadClipboardAndSaveAsync();
    }

    private async Task ReadClipboardAndSaveAsync()
    {
        try
        {
            var result = await clipboardReader.ReadTextAsync();
            if (!result.Succeeded)
            {
                SetStatus(result.Message ?? "无法读取剪贴板。", isSuccess: false);
                return;
            }

            TermTextBox.Text = result.Text;
            await SaveCaptureAsync(ClipboardCaptureParser.ToCapture(result));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            SetStatus($"保存失败：{exception.Message}", isSuccess: false);
        }
        catch (DbException exception)
        {
            SetStatus($"数据库暂时不可用：{exception.Message}", isSuccess: false);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            if (globalHotKeyService.TryRegister(Key.D, out var errorCode))
            {
                SetStatus("置顶窗口已启用 · Ctrl+Shift+D 读取剪贴板", isSuccess: true);
            }
            else
            {
                SetStatus($"快捷键 Ctrl+Shift+D 已被占用（Windows 错误 {errorCode}），请在设置中更换。", isSuccess: false);
            }
        }
        catch (InvalidOperationException exception)
        {
            SetStatus($"快捷键初始化失败：{exception.Message}", isSuccess: false);
        }
    }

    private void HandleGlobalCapture()
    {
        Activate();
        _ = ReadClipboardAndSaveAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        globalHotKeyService.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void SaveInputButton_Click(object sender, RoutedEventArgs e)
    {
        var result = ClipboardCaptureParser.Parse(TermTextBox.Text);
        if (!result.Succeeded)
        {
            SetStatus(result.Message ?? "请输入一个单词或短语。", isSuccess: false);
            return;
        }

        try
        {
            await SaveCaptureAsync(ClipboardCaptureParser.ToCapture(result));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            SetStatus($"保存失败：{exception.Message}", isSuccess: false);
        }
        catch (DbException exception)
        {
            SetStatus($"数据库暂时不可用：{exception.Message}", isSuccess: false);
        }
    }

    private async Task SaveCaptureAsync(NewWordCapture capture)
    {
        var result = await wordRepository.CaptureAsync(capture);
        if (result.RequiresSenseSelection)
        {
            SetStatus($"发现 {result.Candidates.Count} 个同形词候选，请后续选择词义。", isSuccess: false);
            return;
        }

        var action = result.IsNew ? "已记录" : "已更新遇见次数";
        SetStatus($"{action}：{result.Word.Term}（遇见 {result.Word.EncounterCount} 次）", isSuccess: true);
    }

    private void SetStatus(string message, bool isSuccess)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isSuccess
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.IndianRed;
    }
}
