namespace WordPin.Application;

public sealed record DictionaryImportResult(
    long ImportedEntries,
    string ProviderVersion,
    string DatabasePath,
    TimeSpan Elapsed);

public interface IDictionaryImportService
{
    Task<DictionaryImportResult> ImportCsvAsync(
        string csvPath,
        string providerVersion,
        CancellationToken cancellationToken = default);
}
