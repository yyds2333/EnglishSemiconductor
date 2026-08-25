using System.Windows;
using System.IO;
using WordPin.Application;
using WordPin.Infrastructure.Dictionary;
using WordPin.Infrastructure.Learning;

namespace WordPin.App;

public partial class App : System.Windows.Application, IDisposable
{
    private SqliteLearningDatabase? database;
    private SqliteDictionaryStore? dictionaryStore;
    private SqliteDictionaryImportService? dictionaryImportService;
    private MyMemoryTranslationProvider? translationProvider;
    private OpenAiCompatibleDefinitionProvider? languageModelProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordPin");
        database = new SqliteLearningDatabase(Path.Combine(dataDirectory, "wordpin.db"));
        database.InitializeAsync().GetAwaiter().GetResult();
        var dictionaryDirectory = Path.Combine(dataDirectory, "dictionary");
        dictionaryStore = new SqliteDictionaryStore(Path.Combine(dictionaryDirectory, "dictionary.db"));
        dictionaryStore.InitializeAsync().GetAwaiter().GetResult();
        dictionaryImportService = new SqliteDictionaryImportService(Path.Combine(dictionaryDirectory, "dictionary.db"));

        var repository = new SqliteWordRepository(database);
        var queueService = new SqliteStudyQueueService(database);
        var llmSettingsStore = new DpapiLlmSettingsStore(dataDirectory);
        translationProvider = new MyMemoryTranslationProvider(llmSettingsStore);
        languageModelProvider = new OpenAiCompatibleDefinitionProvider(llmSettingsStore);
        var usageStore = new SqliteLlmUsageStore(database);
        var definitionResolver = new DefinitionResolver(
            repository,
            dictionaryStore,
            translationProvider,
            languageModelProvider,
            usageStore);
        var window = new MainWindow(
            new WpfClipboardReader(),
            repository,
            repository,
            definitionResolver,
            queueService,
            dictionaryImportService,
            llmSettingsStore);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        dictionaryStore?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        dictionaryStore = null;
        dictionaryImportService?.Dispose();
        dictionaryImportService = null;
        translationProvider?.Dispose();
        translationProvider = null;
        languageModelProvider?.Dispose();
        languageModelProvider = null;
        database?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        database = null;
        GC.SuppressFinalize(this);
    }
}
