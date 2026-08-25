using System.Windows;
using System.Data.Common;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly IClipboardReader clipboardReader;
    private readonly IWordRepository wordRepository;
    private readonly IDictionaryProvider dictionaryProvider;
    private readonly GlobalHotKeyService globalHotKeyService;
    private readonly DispatcherTimer undoTimer;
    private WordCaptureResult? lastCapture;
    private int undoSecondsRemaining;

    public MainWindow(
        IClipboardReader clipboardReader,
        IWordRepository wordRepository,
        IDictionaryProvider dictionaryProvider)
    {
        this.clipboardReader = clipboardReader ?? throw new ArgumentNullException(nameof(clipboardReader));
        this.wordRepository = wordRepository ?? throw new ArgumentNullException(nameof(wordRepository));
        this.dictionaryProvider = dictionaryProvider ?? throw new ArgumentNullException(nameof(dictionaryProvider));
        InitializeComponent();
        globalHotKeyService = new GlobalHotKeyService(this, hotKeyId: 1001, HandleGlobalCapture);
        undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        undoTimer.Tick += UndoTimer_Tick;
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
        undoTimer.Stop();
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

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (lastCapture is null)
        {
            return;
        }

        try
        {
            var undone = await wordRepository.UndoLastCaptureAsync(lastCapture);
            if (undone)
            {
                SetStatus($"已撤销：{lastCapture.Word.Term}", isSuccess: true);
            }
            else
            {
                SetStatus("撤销未执行：这条记录可能已经发生新的变化。", isSuccess: false);
            }
        }
        catch (DbException exception)
        {
            SetStatus($"撤销失败：{exception.Message}", isSuccess: false);
        }
        finally
        {
            StopUndoWindow();
        }
    }

    private async Task SaveCaptureAsync(NewWordCapture capture)
    {
        var result = await wordRepository.CaptureAsync(capture);
        if (result.RequiresSenseSelection)
        {
            DefinitionTextBlock.Text = "本地释义：请先选择词义候选。";
            SetStatus($"发现 {result.Candidates.Count} 个同形词候选，请后续选择词义。", isSuccess: false);
            return;
        }

        var dictionaryEntry = await dictionaryProvider.LookupAsync(result.Word.Term, result.Word.Language);
        DefinitionTextBlock.Text = dictionaryEntry is null
            ? "本地释义：未找到本地释义"
            : FormatDictionaryEntry(dictionaryEntry);
        var action = result.IsNew ? "已记录" : "已更新遇见次数";
        lastCapture = result;
        undoSecondsRemaining = 5;
        UndoButton.Content = "撤销 (5)";
        UndoButton.Visibility = Visibility.Visible;
        undoTimer.Start();
        SetStatus($"{action}：{result.Word.Term}（遇见 {result.Word.EncounterCount} 次）", isSuccess: true);
    }

    private void UndoTimer_Tick(object? sender, EventArgs e)
    {
        undoSecondsRemaining--;
        if (undoSecondsRemaining <= 0)
        {
            StopUndoWindow();
            return;
        }

        UndoButton.Content = $"撤销 ({undoSecondsRemaining})";
    }

    private void StopUndoWindow()
    {
        undoTimer.Stop();
        lastCapture = null;
        UndoButton.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string message, bool isSuccess)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = isSuccess
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.IndianRed;
    }

    private static string FormatDictionaryEntry(WordPin.Domain.DictionaryEntry entry)
    {
        var translation = string.IsNullOrWhiteSpace(entry.Translation) ? "暂无中文释义" : entry.Translation;
        var definition = string.IsNullOrWhiteSpace(entry.Definition) ? null : entry.Definition;
        var phonetic = string.IsNullOrWhiteSpace(entry.Phonetic) ? null : entry.Phonetic;
        var details = string.Join(" · ", new[] { phonetic, entry.PartOfSpeech }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(definition)
            ? $"本地释义：{translation}{(string.IsNullOrWhiteSpace(details) ? string.Empty : $" ({details})")}"
            : $"本地释义：{translation}\n{definition}";
    }
}
