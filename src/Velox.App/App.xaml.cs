using System.Windows;
using System.Windows.Threading;
using Velox.App.Services;
using Velox.App.Views;
using Velox.Core.Services;

namespace Velox.App;

public partial class App : Application
{
    private const string MutexName = "VeloxDM_SingleInstance_7C6CFF";
    private const string ShowEventName = "VeloxDM_ShowWindow_7C6CFF";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private BridgeServer? _bridge;

    public static DownloadManager Manager { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // já existe uma instância: pede que ela apareça e sai
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ShowEventName);
                evt.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        Manager = new DownloadManager();
        Log.Init(Manager.DataDirectory);
        Log.Info($"Iniciando Velox (args: {string.Join(' ', e.Args)})");
        if (Manager.SettingsLoadError != null) Log.Warn("settings.json inválido, usando padrões: " + Manager.SettingsLoadError);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Exceção não tratada (AppDomain)", args.ExceptionObject as Exception);
        Manager.Start();

        var window = new MainWindow();
        MainWindow = window;

        bool startMinimized = Manager.Settings.StartMinimized ||
                              e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startMinimized) window.Show();
        Log.Info($"Janela principal {(startMinimized ? "iniciada minimizada" : "exibida")}. Estado={window.WindowState}, Visível={window.IsVisible}");

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            Dispatcher.BeginInvoke(() => window.ShowFromTray());
        }, null, -1, false);

        // integração com o navegador: registro do host (idempotente) + ponte por named pipe
        if (!BrowserIntegration.Register(Manager.DataDirectory, out var regError))
            Log.Warn("Integração com navegador não registrada: " + regError);
        _bridge = new BridgeServer(json => window.Dispatcher.InvokeAsync(() => window.HandleBridgeMessage(json)).Task);

        // URLs passadas por linha de comando
        var urls = e.Args.Where(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                     a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)).ToList();
        if (urls.Count > 0)
        {
            window.ShowFromTray();
            window.Dispatcher.BeginInvoke(() => window.OpenAddDialog(urls), DispatcherPriority.ApplicationIdle);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log.Error("Exceção não tratada (Dispatcher)", e.Exception);
        try
        {
            MessageBox.Show(e.Exception.Message, "Velox — erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _bridge?.Dispose();
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            Manager?.ShutdownAsync().GetAwaiter().GetResult();
        }
        catch { }
        finally
        {
            try { _mutex?.ReleaseMutex(); } catch { /* segunda instância nunca adquiriu o mutex */ }
            _mutex?.Dispose();
        }
        base.OnExit(e);
    }
}
