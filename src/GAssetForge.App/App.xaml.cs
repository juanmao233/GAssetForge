using GAssetForge.App.Services;
using GAssetForge.App.ViewModels;
using System.Windows;

namespace GAssetForge.App;

public partial class App : Application
{
    public ShellViewModel? Shell { get; private set; }
    public AvalonDockLayoutService? LayoutService { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new WorkspacePaths();
        var settingsService = new UserSettingsService(paths);
        var themeService = new ThemeService();
        var repository = new JsonProjectRepository(paths);
        var dialogs = new DesktopFileDialogService();
        var history = new ProjectHistoryService();
        var editorWorkspace = new EditorWorkspaceViewModel(repository, dialogs, history);

        Shell = new ShellViewModel(themeService, settingsService, editorWorkspace);
        LayoutService = new AvalonDockLayoutService(paths);

        var window = new MainWindow(Shell, LayoutService);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Shell?.Shutdown();
        base.OnExit(e);
    }
}
