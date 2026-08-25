using System.Windows;
using System.IO;
using WordPin.Infrastructure.Learning;

namespace WordPin.App;

public partial class App : System.Windows.Application, IDisposable
{
    private SqliteLearningDatabase? database;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WordPin");
        database = new SqliteLearningDatabase(Path.Combine(dataDirectory, "wordpin.db"));
        database.InitializeAsync().GetAwaiter().GetResult();

        var repository = new SqliteWordRepository(database);
        var window = new MainWindow(new WpfClipboardReader(), repository);
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
        database?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        database = null;
        GC.SuppressFinalize(this);
    }
}
