using System.Windows;
using WordPin.Domain;

namespace WordPin.App;

public partial class DefinitionEditorWindow : Window
{
    private readonly Guid wordId;
    private readonly SavedDefinition? existing;

    public DefinitionEditorWindow(Guid wordId, string term, SavedDefinition? existing = null)
    {
        this.wordId = wordId;
        this.existing = existing;
        InitializeComponent();
        TermTextBlock.Text = term;
        ChineseDefinitionTextBox.Text = existing?.DefinitionZh;
        EnglishDefinitionTextBox.Text = existing?.DefinitionEn;
        PartOfSpeechTextBox.Text = existing?.PartOfSpeech;
        ExampleTextBox.Text = existing?.Example;
    }

    public DefinitionDraft? Draft { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var chinese = TrimOrNull(ChineseDefinitionTextBox.Text);
        var english = TrimOrNull(EnglishDefinitionTextBox.Text);
        if (chinese is null && english is null)
        {
            ValidationTextBlock.Text = "请至少填写中文释义或英文释义。";
            return;
        }

        Draft = new DefinitionDraft(
            WordId: wordId,
            PartOfSpeech: TrimOrNull(PartOfSpeechTextBox.Text),
            DefinitionZh: chinese,
            DefinitionEn: english,
            Example: TrimOrNull(ExampleTextBox.Text),
            ExistingId: existing?.SourceKind == DefinitionSourceKind.Manual ? existing.Id : null,
            SourceKind: DefinitionSourceKind.Manual,
            Status: DefinitionStatus.Accepted,
            ConfirmedAt: DateTimeOffset.UtcNow);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
