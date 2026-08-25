using System.Windows;
using WordPin.Application;

namespace WordPin.App;

public partial class LlmSettingsWindow : Window
{
    private readonly LlmSettings current;

    public LlmSettingsWindow(LlmSettings current)
    {
        this.current = current;
        InitializeComponent();
        EnabledCheckBox.IsChecked = current.Enabled;
        BaseUrlTextBox.Text = current.BaseUrl;
        ModelTextBox.Text = current.Model;
        DailyLimitTextBox.Text = current.DailyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SendContextCheckBox.IsChecked = current.SendContext;
    }

    public LlmSettings? Settings { get; private set; }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(BaseUrlTextBox.Text.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            ValidationTextBlock.Text = "Base URL 必须是 HTTPS 地址。";
            return;
        }

        if (!int.TryParse(
                DailyLimitTextBox.Text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var dailyLimit)
            || dailyLimit is < 1 or > 500)
        {
            ValidationTextBlock.Text = "每日请求次数必须是 1 到 500 之间的整数。";
            return;
        }

        var key = string.IsNullOrWhiteSpace(ApiKeyPasswordBox.Password)
            ? current.ApiKey
            : ApiKeyPasswordBox.Password.Trim();
        if (EnabledCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(ModelTextBox.Text))
        {
            ValidationTextBlock.Text = "启用 AI 时必须填写模型名称。";
            return;
        }

        if (EnabledCheckBox.IsChecked == true && string.IsNullOrWhiteSpace(key))
        {
            ValidationTextBlock.Text = "启用 AI 时必须填写 API Key。";
            return;
        }

        Settings = new LlmSettings(
            Enabled: EnabledCheckBox.IsChecked == true,
            BaseUrl: baseUri.ToString().TrimEnd('/'),
            Model: ModelTextBox.Text.Trim(),
            ApiKey: key,
            DailyLimit: dailyLimit,
            SendContext: SendContextCheckBox.IsChecked == true);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
