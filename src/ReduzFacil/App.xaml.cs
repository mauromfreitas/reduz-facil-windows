using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;

namespace ReduzFacil;

public partial class App : Application
{
    private readonly DataPaths _data;
    private readonly EngineInfo _engine;
    private readonly string[] _args;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;
    private readonly HashSet<long> _notified = [];

    public SmokeOptions? Smoke { get; }
    public AppServices Services { get; private set; } = null!;

    public static App Instance => (App)Current;

    public App(DataPaths data, EngineInfo engine, string[] args, SmokeOptions? smoke)
    {
        _data = data;
        _engine = engine;
        _args = args;
        Smoke = smoke;

        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = ptBr;
        CultureInfo.DefaultThreadCurrentUICulture = ptBr;
        Thread.CurrentThread.CurrentCulture = ptBr;
        Thread.CurrentThread.CurrentUICulture = ptBr;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(ptBr.IetfLanguageTag)));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erro não tratado", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Erro em tarefa", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services = new AppServices(_data, _engine);
        WindowsIntegration.RepairIfMoved(Services.Settings.ShellMenu, Services.Settings.StartWithWindows);
        Services.RestartWatcher();

        _tray = new TrayIcon(this);
        Services.WatchStateChanged += () => Dispatcher.BeginInvoke(UpdateTray);
        Services.Queue.Idle += () => Dispatcher.BeginInvoke(OnQueueIdle);

        _window = new MainWindow(Services);
        MainWindow = _window;

        var req = AppServices.ParseArgs(_args);
        if (req.Tray && Services.Settings.Watch.Enabled && req.Files.Length == 0) Log.Info("Aberto na bandeja (inicialização do Windows)");
        else _window.Show();
        UpdateTray();
        Handle(req);

        if (Smoke is not null) _ = new SmokeRunner(this, _window, Services, Smoke).RunAsync();
    }

    /// <summary>Outra abertura do .exe (botão direito do Explorer, atalho) mandou argumentos.</summary>
    public void OnExternalArgs(string[] args) => Dispatcher.BeginInvoke(() =>
    {
        var req = AppServices.ParseArgs(args);
        Handle(req);
        if (req.Show || req.Files.Length > 0) ShowWindow();
    });

    private void Handle(LaunchRequest req)
    {
        if (req.Files.Length == 0) return;
        var options = Services.OptionsFor(req.OnlyAdd ? null : req.Preset);
        var n = Services.Enqueue(req.Files, options, JobOrigin.ShellMenu);
        _window?.ShowReduzir();
        if (n == 0) _window?.Toast("Nenhum arquivo suportado (fotos, vídeos, áudios e PDFs).");
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>Fechar a janela: vai para a bandeja se a pasta vigiada estiver ligada.</summary>
    public bool ShouldHideInsteadOfClose() =>
        !_exiting && Services.Settings.TrayOnClose && Services.WatchRunning;

    public void ExitApp(int exitCode = 0)
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            Services.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao fechar", ex);
        }
        _tray?.Dispose();
        _tray = null;
        Log.Info("Fechando");
        Shutdown(exitCode);
    }

    // Ícone sempre perto do relógio: mostra os avisos de "Pronto!" e mantém a pasta vigiada acessível.
    private void UpdateTray() => _tray?.Update(true);

    public void OnWindowHidden()
    {
        UpdateTray();
        _tray?.ShowBalloon("O Reduz Fácil continua vigiando a pasta", "Clique no ícone perto do relógio para abrir.");
    }

    private void OnQueueIdle()
    {
        if (_window is null || !Services.Settings.NotifyWhenDone) return;
        // Só o que terminou desde o último aviso.
        var finished = Services.Queue.Items.Where(i => i.IsFinished && i.Result is not null && !_notified.Contains(i.Id)).ToList();
        foreach (var i in finished) _notified.Add(i.Id);
        if (finished.Count == 0) return;
        if (_window.IsVisible && _window.IsActive) return;
        var saved = finished.Sum(i => i.Result!.KeptOriginal ? 0 : Math.Max(0, i.Result.OriginalBytes - i.Result.FinalBytes));
        _tray?.ShowBalloon("Pronto!", $"{finished.Count} arquivo(s) reduzido(s), {Format.Bytes(saved)} economizados.");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        if (Smoke is null)
            MessageBox.Show("Algo deu errado no Reduz Fácil.\n\n" +
                            $"Detalhe: {e.Exception.Message}\n\n" +
                            $"O registro está em {DataFolder.FolderName}\\logs\\app.log.",
                AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
