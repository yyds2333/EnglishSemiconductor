using System.Windows;
using System.Data.Common;
using System.IO;
using WordPin.Application;
using WordPin.Domain;

namespace WordPin.App;

public partial class MainWindow : Window
{
    private readonly IClipboardReader clipboardReader;
    private readonly IWordRepository wordRepository;

    public MainWindow(IClipboardReader clipboardReader, IWordRepository wordRepository)
    {
        this.clipboardReader = clipboardReader ?? throw new ArgumentNullException(nameof(clipboardReader));
        this.wordRepository = wordRepository ?? throw new ArgumentNullException(nameof(wordRepository));
        InitializeComponent();
    }

    private async void ReadClipboardButton_Click(object sender, RoutedEventArgs e)
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
