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

        var repository = new SqliteWordRepository(database);
        var queueService = new SqliteStudyQueueService(database);
        var window = new MainWindow(new WpfClipboardReader(), repository, dictionaryStore, queueService);
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
        database?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        database = null;
        GC.SuppressFinalize(this);
    }
}
