using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using WordPin.Application;

namespace WordPin.App;

/// <summary>
/// Stores non-secret remote lookup settings as JSON and protects the AI key
/// with the current Windows user's DPAPI profile.
/// </summary>
public sealed class DpapiLlmSettingsStore : ILlmSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WordPin-Llm-Settings-v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string settingsPath;

    public DpapiLlmSettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        settingsPath = Path.Combine(Path.GetFullPath(dataDirectory), "llm-settings.json");
    }

    public LlmSettings Load()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new LlmSettings();
            }

            var persisted = JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(settingsPath));
            if (persisted is null)
            {
                return new LlmSettings();
            }

            return new LlmSettings(
                Enabled: persisted.Enabled,
                BaseUrl: persisted.BaseUrl ?? "https://api.openai.com/v1",
                Model: persisted.Model ?? string.Empty,
                ApiKey: Unprotect(persisted.ApiKeyProtected),
                DailyLimit: Math.Clamp(persisted.DailyLimit, 1, 500),
                SendContext: persisted.SendContext,
                TimeoutSeconds: Math.Clamp(persisted.TimeoutSeconds == 0 ? 15 : persisted.TimeoutSeconds, 5, 60),
                OnlineTranslationEnabled: persisted.OnlineTranslationEnabled ?? true,
                OnlineMachineTranslationEnabled: persisted.OnlineMachineTranslationEnabled ?? false,
                OnlineTranslationBaseUrl: persisted.OnlineTranslationBaseUrl ?? "https://api.mymemory.translated.net",
                OnlineTranslationDailyLimit: Math.Clamp(persisted.OnlineTranslationDailyLimit ?? 60, 1, 500),
                OnlineTranslationTimeoutSeconds: Math.Clamp(persisted.OnlineTranslationTimeoutSeconds ?? 10, 5, 60));
        }
        catch (Exception) when (File.Exists(settingsPath))
        {
            return new LlmSettings();
        }
    }

    public void Save(LlmSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("AI settings directory is not configured.");
        Directory.CreateDirectory(directory);
        var persisted = new PersistedSettings(
            Enabled: settings.Enabled,
            BaseUrl: settings.BaseUrl.Trim(),
            Model: settings.Model.Trim(),
            DailyLimit: Math.Clamp(settings.DailyLimit, 1, 500),
            SendContext: settings.SendContext,
            TimeoutSeconds: Math.Clamp(settings.TimeoutSeconds, 5, 60),
            OnlineTranslationEnabled: settings.OnlineTranslationEnabled,
            OnlineMachineTranslationEnabled: settings.OnlineMachineTranslationEnabled,
            OnlineTranslationBaseUrl: settings.OnlineTranslationBaseUrl.Trim(),
            OnlineTranslationDailyLimit: Math.Clamp(settings.OnlineTranslationDailyLimit, 1, 500),
            OnlineTranslationTimeoutSeconds: Math.Clamp(settings.OnlineTranslationTimeoutSeconds, 5, 60),
            ApiKeyProtected: Protect(settings.ApiKey));
        var tempPath = settingsPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(persisted, JsonOptions));
        File.Move(tempPath, settingsPath, overwrite: true);
    }

    private static string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value.Trim()),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(value),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record PersistedSettings(
        bool Enabled,
        string? BaseUrl,
        string? Model,
        int DailyLimit,
        bool SendContext,
        int TimeoutSeconds,
        bool? OnlineTranslationEnabled,
        bool? OnlineMachineTranslationEnabled,
        string? OnlineTranslationBaseUrl,
        int? OnlineTranslationDailyLimit,
        int? OnlineTranslationTimeoutSeconds,
        string? ApiKeyProtected);
}
