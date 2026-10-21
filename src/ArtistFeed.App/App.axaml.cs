using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ArtistFeed.App.ViewModels;
using ArtistFeed.App.Views;
using ArtistFeed.Core;

namespace ArtistFeed.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var folder = Engine.DefaultDataFolder();
            var dbPath = Path.Combine(folder, "feed.db");
            Directory.CreateDirectory(folder);
            Engine.FinishImport(dbPath);                     // a database imported in Settings takes over now
            var engine = new Engine(dbPath);
            var vm = new MainViewModel(engine, folder);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => engine.Dispose();
            engine.StartSchedule();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
