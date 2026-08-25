using WordPin.Infrastructure.Dictionary;

var options = ImportOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("Usage: WordPin.DictionaryImport --csv <path> --database <path> --version <version>");
    Environment.ExitCode = 2;
    return;
}

if (!File.Exists(options.CsvPath))
{
    Console.Error.WriteLine($"CSV file does not exist: {options.CsvPath}");
    Environment.ExitCode = 2;
    return;
}

await using var csvStream = File.OpenRead(options.CsvPath);
await using var store = new SqliteDictionaryStore(options.DatabasePath);
await store.ImportAsync(EcdictCsvReader.ReadAsync(csvStream, options.ProviderVersion));

Console.WriteLine($"Imported {await store.CountAsync()} entries into {options.DatabasePath}");

internal sealed record ImportOptions(string CsvPath, string DatabasePath, string ProviderVersion)
{
    public static ImportOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return null;
            }

            values[args[index]] = args[index + 1];
        }

        return values.TryGetValue("--csv", out var csvPath)
            && values.TryGetValue("--database", out var databasePath)
            && values.TryGetValue("--version", out var providerVersion)
            ? new ImportOptions(Path.GetFullPath(csvPath), Path.GetFullPath(databasePath), providerVersion)
            : null;
    }
}
