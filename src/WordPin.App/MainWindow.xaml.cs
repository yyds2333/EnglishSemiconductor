using System.Windows;
using System.Data.Common;
using Microsoft.Win32;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using WordPin.Application;
using WordPin.Domain;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace WordPin.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly IClipboardReader clipboardReader;
    private readonly IWordRepository wordRepository;
    private readonly IDefinitionRepository definitionRepository;
    private readonly IDefinitionResolver definitionResolver;
    private readonly IStudyQueueService studyQueueService;
    private readonly IDictionaryImportService dictionaryImportService;
    private readonly ILlmSettingsStore llmSettingsStore;
    private readonly GlobalHotKeyService globalHotKeyService;
    private readonly DispatcherTimer undoTimer;
    private WordCaptureResult? lastCapture;
    private WordRecord? currentWord;
    private int undoSecondsRemaining;

    public MainWindow(
        IClipboardReader clipboardReader,
        IWordRepository wordRepository,
        IDefinitionRepository definitionRepository,
        IDefinitionResolver definitionResolver,
        IStudyQueueService studyQueueService,
        IDictionaryImportService dictionaryImportService,
        ILlmSettingsStore llmSettingsStore)
    {
        this.clipboardReader = clipboardReader ?? throw new ArgumentNullException(nameof(clipboardReader));
        this.wordRepository = wordRepository ?? throw new ArgumentNullException(nameof(wordRepository));
        this.definitionRepository = definitionRepository ?? throw new ArgumentNullException(nameof(definitionRepository));
        this.definitionResolver = definitionResolver ?? throw new ArgumentNullException(nameof(definitionResolver));
        this.studyQueueService = studyQueueService ?? throw new ArgumentNullException(nameof(studyQueueService));
        this.dictionaryImportService = dictionaryImportService ?? throw new ArgumentNullException(nameof(dictionaryImportService));
        this.llmSettingsStore = llmSettingsStore ?? throw new ArgumentNullException(nameof(llmSettingsStore));
        InitializeComponent();
        globalHotKeyService = new GlobalHotKeyService(this, hotKeyId: 1001, HandleGlobalCapture);
        undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        undoTimer.Tick += UndoTimer_Tick;
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
    }

    private async void ReadClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        await ReadClipboardAndSaveAsync();
    }

    private async void ImportDictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Win32OpenFileDialog
        {
            Title = "选择本地 CSV 词典",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetStatus("正在导入词典，请稍候…", isSuccess: true);
            var version = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var result = await dictionaryImportService.ImportCsvAsync(dialog.FileName, version);
            SetStatus($"词典导入完成：{result.ImportedEntries:N0} 条（{result.Elapsed.TotalSeconds:F1} 秒）", isSuccess: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or DbException)
        {
            SetStatus($"词典导入失败：{exception.Message}", isSuccess: false);
        }
    }

    private void LlmSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LlmSettingsWindow(llmSettingsStore.Load())
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true && dialog.Settings is not null)
        {
            llmSettingsStore.Save(dialog.Settings);
            SetStatus(dialog.Settings.Enabled ? "AI 释义补全已启用。" : "AI 释义补全已关闭。", isSuccess: true);
        }
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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var localDate = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var snapshot = await studyQueueService.GetOrCreateAsync(localDate, DateTimeOffset.UtcNow);
            SetStatus($"今日队列 {snapshot.Items.Count} 个 · Ctrl+Shift+D 读取剪贴板", isSuccess: true);
        }
        catch (DbException exception)
        {
            SetStatus($"今日队列暂时不可用：{exception.Message}", isSuccess: false);
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

    private async void ReviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentWord is null || sender is not Button button || button.Tag is not string feedbackText
            || !Enum.TryParse<ReviewFeedback>(feedbackText, ignoreCase: true, out var feedback))
        {
            return;
        }

        try
        {
            var result = await wordRepository.ReviewAsync(currentWord.Id, feedback, DateTimeOffset.UtcNow);
            currentWord = result.Word;
            UpdateMasteryDisplay(result.Evaluation.After);
            StopUndoWindow();
            SetStatus(
                $"已记录 {feedbackText.ToUpperInvariant()}：L{result.Word.MasteryLevel} · {result.Word.MasteryScore} 分",
                isSuccess: true);
        }
        catch (DbException exception)
        {
            SetStatus($"复习保存失败：{exception.Message}", isSuccess: false);
        }
    }

    private async void EditDefinitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentWord is null)
        {
            return;
        }

        var definitions = await definitionRepository.GetForWordAsync(currentWord.Id);
        var existing = definitions.FirstOrDefault(definition => definition.Status == DefinitionStatus.Accepted)
            ?? (definitions.Count > 0 ? definitions[0] : null);
        var editor = new DefinitionEditorWindow(currentWord.Id, currentWord.Term, existing)
        {
            Owner = this
        };
        if (editor.ShowDialog() != true || editor.Draft is null)
        {
            return;
        }

        try
        {
            await definitionRepository.SaveAsync(editor.Draft);
            await RefreshDefinitionAsync(currentWord);
            SetStatus("已保存自定义释义。", isSuccess: true);
        }
        catch (DbException exception)
        {
            SetStatus($"释义保存失败：{exception.Message}", isSuccess: false);
        }
    }

    private async void RestoreDefinitionButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentWord is null)
        {
            return;
        }

        try
        {
            var definitions = await definitionRepository.GetForWordAsync(currentWord.Id);
            foreach (var definition in definitions)
            {
                await definitionRepository.DeleteAsync(definition.Id);
            }

            await RefreshDefinitionAsync(currentWord);
            SetStatus("已恢复本地词典释义。", isSuccess: true);
        }
        catch (DbException exception)
        {
            SetStatus($"恢复本地释义失败：{exception.Message}", isSuccess: false);
        }
    }

    private async void AcceptAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentWord is null)
        {
            return;
        }

        try
        {
            var candidate = (await definitionRepository.GetForWordAsync(currentWord.Id))
                .FirstOrDefault(definition => definition.SourceKind == DefinitionSourceKind.LanguageModel
                    && definition.Status == DefinitionStatus.Proposed);
            if (candidate is null)
            {
                return;
            }

            await definitionRepository.SaveAsync(new DefinitionDraft(
                WordId: currentWord.Id,
                PartOfSpeech: candidate.PartOfSpeech,
                DefinitionZh: candidate.DefinitionZh,
                DefinitionEn: candidate.DefinitionEn,
                Example: candidate.Example,
                SortOrder: candidate.SortOrder,
                SourceKind: DefinitionSourceKind.LanguageModel,
                Status: DefinitionStatus.Accepted,
                SourceDetail: candidate.SourceDetail,
                ModelName: candidate.ModelName,
                PromptVersion: candidate.PromptVersion,
                GeneratedAt: candidate.GeneratedAt,
                ConfirmedAt: DateTimeOffset.UtcNow,
                ExistingId: candidate.Id));
            await RefreshDefinitionAsync(currentWord);
            SetStatus("已采用 AI 释义。", isSuccess: true);
        }
        catch (DbException exception)
        {
            SetStatus($"采用 AI 释义失败：{exception.Message}", isSuccess: false);
        }
    }

    private async void RetryAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (currentWord is null)
        {
            return;
        }

        try
        {
            var definitions = await definitionRepository.GetForWordAsync(currentWord.Id);
            foreach (var definition in definitions.Where(definition =>
                         definition.SourceKind == DefinitionSourceKind.LanguageModel
                         && definition.Status == DefinitionStatus.Proposed))
            {
                await definitionRepository.DeleteAsync(definition.Id);
            }

            await RefreshDefinitionAsync(currentWord);
            SetStatus("已请求重新生成 AI 释义。", isSuccess: true);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException or InvalidDataException or TaskCanceledException)
        {
            SetStatus($"重新生成 AI 释义失败：{exception.Message}", isSuccess: false);
        }
    }

    private async Task SaveCaptureAsync(NewWordCapture capture)
    {
        var result = await wordRepository.CaptureAsync(capture);
        if (result.RequiresSenseSelection)
        {
            currentWord = null;
            ReviewPanel.Visibility = Visibility.Collapsed;
            DefinitionTextBlock.Text = "本地释义：请先选择词义候选。";
            SetStatus($"发现 {result.Candidates.Count} 个同形词候选，请后续选择词义。", isSuccess: false);
            return;
        }

        await RefreshDefinitionAsync(result.Word);
        currentWord = result.Word;
        UpdateMasteryDisplay(new MasteryState(
            Score: result.Word.MasteryScore,
            Level: result.Word.MasteryLevel));
        ReviewPanel.Visibility = Visibility.Visible;
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

    private async Task RefreshDefinitionAsync(WordRecord word)
    {
        try
        {
            var resolution = await definitionResolver.ResolveAsync(word);
            var remotePreferred = resolution.Definitions.Count > 0 ? resolution.Definitions[0] : null;
            if (remotePreferred is not null)
            {
                DefinitionTextBlock.Text = FormatSavedDefinition(remotePreferred);
                EditDefinitionButton.Content = remotePreferred.Status == DefinitionStatus.Proposed ? "编辑并采用" : "编辑释义";
                EditDefinitionButton.Visibility = Visibility.Visible;
                RestoreDefinitionButton.Visibility = Visibility.Visible;
                var isProposed = remotePreferred.Status == DefinitionStatus.Proposed
                    && remotePreferred.SourceKind == DefinitionSourceKind.LanguageModel;
                AcceptAiButton.Visibility = isProposed ? Visibility.Visible : Visibility.Collapsed;
                RetryAiButton.Visibility = isProposed ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            DefinitionTextBlock.Text = resolution.DictionaryEntry is null
                ? "本地释义：未找到释义（可点击添加释义或配置 AI 补全）"
                : FormatDictionaryEntry(resolution.DictionaryEntry);
            EditDefinitionButton.Content = "添加释义";
            EditDefinitionButton.Visibility = Visibility.Visible;
            RestoreDefinitionButton.Visibility = Visibility.Collapsed;
            AcceptAiButton.Visibility = Visibility.Collapsed;
            RetryAiButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is System.Net.Http.HttpRequestException or InvalidDataException or InvalidOperationException or TaskCanceledException)
        {
            DefinitionTextBlock.Text = "本地释义：未找到；AI 补全暂时不可用（可点击添加释义）";
            EditDefinitionButton.Content = "添加释义";
            EditDefinitionButton.Visibility = Visibility.Visible;
            RestoreDefinitionButton.Visibility = Visibility.Collapsed;
            AcceptAiButton.Visibility = Visibility.Collapsed;
            RetryAiButton.Visibility = Visibility.Collapsed;
            SetStatus($"释义查询失败：{exception.Message}", isSuccess: false);
        }
    }

    private void UpdateMasteryDisplay(MasteryState state)
    {
        MasteryTextBlock.Text = $"熟练度：L{state.Level} · {state.Score} 分";
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

    private static string FormatSavedDefinition(SavedDefinition definition)
    {
        var source = definition.SourceKind == DefinitionSourceKind.LanguageModel
            ? definition.Status == DefinitionStatus.Proposed ? "AI 生成 · 未确认" : "AI 已确认"
            : "用户编辑";
        var parts = new List<string> { $"{source}：" };
        if (!string.IsNullOrWhiteSpace(definition.DefinitionZh))
        {
            parts.Add(definition.DefinitionZh.Trim());
        }

        if (!string.IsNullOrWhiteSpace(definition.DefinitionEn))
        {
            parts.Add($"\n{definition.DefinitionEn.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(definition.Example))
        {
            parts.Add($"\n例句：{definition.Example.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(definition.PartOfSpeech))
        {
            parts.Add($" ({definition.PartOfSpeech.Trim()})");
        }

        return string.Concat(parts);
    }
}
