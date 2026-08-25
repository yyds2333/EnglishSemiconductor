using WordPin.Application;

namespace WordPin.Infrastructure.Dictionary;

/// <summary>
/// Imports a CSV into a staging database and atomically replaces the active
/// local dictionary. The active database is never partially populated.
/// </summary>
public sealed class SqliteDictionaryImportService : IDictionaryImportService, IDisposable
{
    private readonly string targetDatabasePath;
    private readonly SemaphoreSlim importLock = new(1, 1);

    public SqliteDictionaryImportService(string targetDatabasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDatabasePath);
        this.targetDatabasePath = Path.GetFullPath(targetDatabasePath);
    }

    public async Task<DictionaryImportResult> ImportCsvAsync(
        string csvPath,
        string providerVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerVersion);
        var sourcePath = Path.GetFullPath(csvPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("CSV file does not exist.", sourcePath);
        }

        await importLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var startedAt = DateTimeOffset.UtcNow;
        var stagingPath = $"{targetDatabasePath}.{Guid.NewGuid():N}.staging";
        try
        {
            var targetDirectory = Path.GetDirectoryName(targetDatabasePath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new InvalidOperationException("Dictionary database directory is not configured.");
            }

            Directory.CreateDirectory(targetDirectory);
            await using (var source = File.OpenRead(sourcePath))
            await using (var stagingStore = new SqliteDictionaryStore(stagingPath))
            {
                await stagingStore.ImportAsync(
                    EcdictCsvReader.ReadAsync(source, providerVersion, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            var importedEntries = await CountAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            var backupPath = Path.Combine(
                targetDirectory,
                $"dictionary-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
            if (File.Exists(targetDatabasePath))
            {
                File.Replace(stagingPath, targetDatabasePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(stagingPath, targetDatabasePath);
            }

            PruneBackups(targetDirectory, keep: 3);

            return new DictionaryImportResult(
                ImportedEntries: importedEntries,
                ProviderVersion: providerVersion,
                DatabasePath: targetDatabasePath,
                Elapsed: DateTimeOffset.UtcNow - startedAt);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }

            importLock.Release();
        }
    }

    public void Dispose() => importLock.Dispose();

    private static async Task<long> CountAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var store = new SqliteDictionaryStore(databasePath);
        return await store.CountAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void PruneBackups(string directory, int keep)
    {
        var backups = Directory.EnumerateFiles(directory, "dictionary-*.db")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(keep)
            .ToArray();
        foreach (var backup in backups)
        {
            File.Delete(backup);
        }
    }
}
