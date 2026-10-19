using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Diagnostics;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Settings;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher;

/// <summary>
/// Arranque: handshake de auto-actualización, logs, auto-instalación del exe, instancia única, manejadores globales de
/// errores, limpieza y ventana principal. Los restos de la auto-actualización ("&lt;exe&gt;.old"…) se borran solo cuando la
/// ventana ya se ha dibujado; si en un arranque "--post-update" la ventana no llega a abrirse, se vuelve a la versión
/// anterior (LauncherSelfUpdater.TryRestorePreviousVersion), se marca esta versión como omitida y se abre la anterior.
/// </summary>
public partial class App : Application
{
    // Los MessageBox no tienen botón «Crear informe» (y sin ventana no hay menú): textos propios.
    private const string StartupFailedTitle = "No se pudo abrir Palito Launcher.";
    private const string RolledBackMessage =
        "La nueva versión de Palito Launcher no pudo abrirse. Se vuelve a la versión anterior.";

    private SingleInstance? _singleInstance;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private int _errorDialogOpen;
    private bool _isPostUpdateStart;
    private string? _launcherExePath;
    private bool _windowRendered;
    private bool _rollbackAttempted;

    /// <summary>La aplicación se está cerrando por código (Shutdown): la ventana no debe pedir confirmación.</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>Windows está cerrando la sesión o apagándose.</summary>
    public static bool IsSessionEnding { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 1. PRIMERA instrucción: avisar a la versión anterior (auto-actualización) de que esta arrancó.
        Exception? signalError = null;
        try
        {
            LauncherSelfUpdater.SignalAlive();
        }
        catch (Exception ex)
        {
            signalError = ex; // el log aún no está listo: se registra justo después
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var args = e.Args ?? Array.Empty<string>();
        _isPostUpdateStart = args.Any(a => string.Equals(a, LauncherSelfUpdater.PostUpdateArgument, StringComparison.OrdinalIgnoreCase));
        // Se guarda antes de cualquier renombrado del exe.
        _launcherExePath = LauncherSelfUpdater.CurrentExePath;

        // 2. Carpetas de datos, log y cabecera con el entorno.
        AppPaths.EnsureCreated();
        try
        {
            Log.Initialize(AppPaths.LogsDir);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"No se pudo inicializar el log: {ex}");
        }
        if (signalError != null) Log.Warn("SignalAlive falló.", signalError);
        LogStartupHeader(args);

        // 5. Manejadores globales (se registran ya para cubrir también el resto del arranque).
        RegisterGlobalErrorHandlers();

        // 3. Auto-instalación del exe en %LOCALAPPDATA%\Programs\PalitoLauncher.
        var outcome = LauncherInstallOutcome.Continue;
        try
        {
            outcome = LauncherInstallation.EnsureInstalled(args);
        }
        catch (Exception ex)
        {
            Log.Error("EnsureInstalled falló; se arranca en el sitio.", ex);
        }
        if (outcome == LauncherInstallOutcome.RelaunchedInstalledCopy)
        {
            Log.Info("Se abrió la copia instalada del launcher; este proceso se cierra.");
            ExitNow();
            return;
        }

        // 4. Instancia única.
        try
        {
            _singleInstance = SingleInstance.TryAcquire(args);
        }
        catch (Exception ex)
        {
            Log.Error("Error en la comprobación de instancia única.", ex);
            _singleInstance = null;
        }
        if (_singleInstance == null)
        {
            ExitNow();
            return;
        }

        SessionEnding += OnSessionEnding;

        // 6. Limpieza en segundo plano (los restos de la auto-actualización, al dibujarse la ventana).
        StartBackgroundCleanup();

        // 7. Ventana enseguida y luego la inicialización.
        try
        {
            _viewModel = new MainViewModel();
            _viewModel.ShutdownRequested += (_, _) => ExitNow();
            _window = new MainWindow(_viewModel);
            _window.ContentRendered += OnMainWindowRendered;
            MainWindow = _window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _window.Show();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear la ventana principal.", ex);
            if (TryRollbackBrokenUpdate()) return;
            ShowStartupFailure(ex);
            ExitNow();
            return;
        }

        _singleInstance.StartListening(OnActivationRequested);
        _ = InitializeViewModelAsync(_viewModel);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try
        {
            _viewModel?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error al cerrar el ViewModel.", ex);
        }

        try
        {
            _singleInstance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Error al liberar la instancia única.", ex);
        }

        Log.Info($"Launcher cerrado (código {e.ApplicationExitCode}).");
        base.OnExit(e);
    }

    /// <summary>Cierra la aplicación sin preguntas.</summary>
    private void ExitNow()
    {
        IsExiting = true;
        try
        {
            Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Error al cerrar la aplicación.", ex);
            Environment.Exit(0);
        }
    }

    private static async Task InitializeViewModelAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            // InitializeAsync ya no lanza; red de seguridad.
            Log.Error("Error inesperado en la inicialización.", ex);
        }
    }

    private static void LogStartupHeader(string[] args)
    {
        Log.Info($"===== {LauncherConfig.LauncherName} arrancando (args: {(args.Length == 0 ? "-" : string.Join(" ", args))}) =====");
        try
        {
            foreach (var pair in DiagnosticReport.GetEnvironmentSummary())
                Log.Info($"{pair.Key}: {pair.Value}");
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo obtener el resumen del entorno.", ex);
        }
    }

    private static void StartBackgroundCleanup()
    {
        _ = Task.Run(() =>
        {
            try
            {
                FileSystemUtil.CleanupStaleTempFiles(AppPaths.AppDataDir, TimeSpan.FromHours(1));
                FileSystemUtil.CleanupStaleTempFiles(AppPaths.CacheDir, TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                Log.Warn("Error al limpiar archivos temporales.", ex);
            }
        });
    }

    /// <summary>
    /// La ventana ya se dibujó: la versión funciona y se pueden borrar los restos de la auto-actualización, incluida la
    /// copia "&lt;exe&gt;.old" para volver atrás.
    /// </summary>
    private void OnMainWindowRendered(object? sender, EventArgs e)
    {
        if (_windowRendered) return;
        _windowRendered = true;
        try
        {
            LauncherSelfUpdater.CleanupOldVersions();
        }
        catch (Exception ex)
        {
            Log.Warn("Error al limpiar versiones antiguas del launcher.", ex);
        }
    }

    // ───────────── Arranque fallido ─────────────

    /// <summary>
    /// Arranque "--post-update" cuya ventana no pudo abrirse: vuelve a la versión anterior si queda "&lt;exe&gt;.old",
    /// marca esta versión como omitida, avisa, abre la anterior (sin "--post-update") y cierra este proceso. False si no
    /// era un arranque tras actualizar o no se pudo volver atrás (entonces no se ha tocado nada más). Una sola vez.
    /// </summary>
    private bool TryRollbackBrokenUpdate()
    {
        if (!_isPostUpdateStart || _rollbackAttempted || IsExiting) return false;
        _rollbackAttempted = true;

        var exe = _launcherExePath;
        var committed = false;
        try
        {
            if (!LauncherSelfUpdater.TryRestorePreviousVersion(exe)) return false;

            // Ya no hay vuelta atrás: sin más avisos de error ni confirmaciones al cerrar, y sin la ventana rota detrás.
            committed = true;
            IsExiting = true;
            try
            {
                _window?.Hide();
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo ocultar la ventana rota.", ex);
            }

            // Sin más guardados del ViewModel durante la vuelta atrás.
            try
            {
                _viewModel?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Error al cerrar el ViewModel antes de volver a la versión anterior.", ex);
            }

            MarkCurrentVersionSkipped();

            // La versión anterior debe poder tomar el mutex (se suelta desde este hilo, que es su dueño).
            try
            {
                _singleInstance?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Error al liberar la instancia única antes de volver a la versión anterior.", ex);
            }
            _singleInstance = null;

            MessageBox.Show(RolledBackMessage, LauncherConfig.LauncherName, MessageBoxButton.OK, MessageBoxImage.Warning);

            if (!StartLauncherProcess(exe!))
            {
                MessageBox.Show("No se pudo abrir la versión anterior. Ábrela de nuevo desde su acceso directo.",
                    LauncherConfig.LauncherName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            ExitNow();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Error al volver a la versión anterior del launcher.", ex);
            if (!committed) return false;

            // Los archivos ya están cambiados: esta versión no debe seguir abierta.
            ExitNow();
            return true;
        }
    }

    /// <summary>Guarda SkippedLauncherVersion = versión actual para que la anterior no vuelva a ofrecerla. Nunca lanza.</summary>
    private static void MarkCurrentVersionSkipped()
    {
        try
        {
            var store = new SettingsStore(AppPaths.SettingsFile);
            var settings = store.Load();
            settings.SkippedLauncherVersion = LauncherSelfUpdater.CurrentVersionText;
            if (store.Save(settings))
                Log.Info($"Versión {settings.SkippedLauncherVersion} del launcher marcada como omitida.");
            else
                Log.Warn("No se pudo marcar como omitida la versión rota del launcher.");
        }
        catch (Exception ex)
        {
            Log.Warn("Error al marcar como omitida la versión rota del launcher.", ex);
        }
    }

    /// <summary>Abre el launcher en <paramref name="exePath"/> sin argumentos. Nunca lanza.</summary>
    private static bool StartLauncherProcess(string exePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? "",
            });
            Log.Info($"Abierta la versión anterior del launcher (pid {process?.Id.ToString() ?? "?"}).");
            return process != null;
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo abrir {exePath}.", ex);
            return false;
        }
    }

    /// <summary>
    /// No hay ventana: crea el informe para el desarrollador en el Escritorio (sin pedir nada) y dice dónde está; si no se
    /// puede, indica la carpeta de logs. Nunca lanza.
    /// </summary>
    private void ShowStartupFailure(Exception error)
    {
        string message;
        string? zip = TryCreateStartupReport(error);
        if (zip != null)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var onDesktop = !string.IsNullOrEmpty(desktop) &&
                            string.Equals(Path.GetDirectoryName(zip), desktop.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            message = onDesktop
                ? $"{StartupFailedTitle}\n\nSe ha creado un informe en el Escritorio:\n{Path.GetFileName(zip)}\n\nEnvíaselo al desarrollador."
                : $"{StartupFailedTitle}\n\nSe ha creado un informe:\n{zip}\n\nEnvíaselo al desarrollador.";
        }
        else
        {
            message = $"{StartupFailedTitle}\n\nSi se repite, envía al desarrollador la carpeta:\n{AppPaths.LogsDir}";
        }

        try
        {
            MessageBox.Show(message, LauncherConfig.LauncherName, MessageBoxButton.OK, MessageBoxImage.Error);
            if (zip != null) ShellService.RevealInExplorer(zip);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo mostrar el aviso de arranque fallido.", ex);
        }
    }

    /// <summary>Informe en el Escritorio (o en la carpeta de datos si no hay Escritorio). Null si falla. Nunca lanza.</summary>
    private string? TryCreateStartupReport(Exception error)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outputDir = !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop) ? desktop : AppPaths.AppDataDir;
            var context = new DiagnosticContext
            {
                Summary = new Dictionary<string, string?>
                {
                    ["Problema"] = "No se pudo abrir la ventana principal",
                    ["Error"] = $"{error.GetType().FullName}: {error.Message}",
                    ["Arranque tras actualizar"] = _isPostUpdateStart ? "sí" : "no",
                },
            };

            // CreateAsync trabaja en Task.Run: esperar aquí no bloquea nada más (aún no hay ventana).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var task = DiagnosticReport.CreateAsync(context, outputDir, cts.Token);
            if (!task.Wait(TimeSpan.FromSeconds(25)))
            {
                Log.Warn("El informe de arranque fallido tardó demasiado.");
                return null;
            }

            Log.Info($"Informe de arranque fallido creado: {task.Result}");
            return task.Result;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo crear el informe de arranque fallido.", ex);
            return null;
        }
    }

    // ───────────── Instancia única ─────────────

    /// <summary>Llega desde un hilo de fondo: trae la ventana al frente y confirma a la otra instancia.</summary>
    private void OnActivationRequested()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _window?.BringToFront();
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo mostrar la ventana a petición de otra instancia.", ex);
            }
            finally
            {
                _singleInstance?.SignalActivated();
            }
        }));
    }

    // ───────────── Errores globales y cierre de sesión ─────────────

    private void RegisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error($"Excepción no controlada (AppDomain, terminando: {e.IsTerminating}).", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Excepción no observada en una tarea.", e.Exception);
            e.SetObserved();
        };
        CommandErrors.Report = _ => ShowFriendlyError();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Excepción no controlada en la interfaz.", e.Exception);
        e.Handled = true;

        // Tras actualizar, un fallo antes de que la ventana llegue a dibujarse = versión rota: volver a la anterior.
        if (!_windowRendered && TryRollbackBrokenUpdate()) return;

        ShowFriendlyError();
    }

    /// <summary>
    /// Muestra el mensaje genérico (uno a la vez). Dentro de la ventana si existe (con «Crear informe»); si no, MessageBox
    /// con la carpeta de logs (un MessageBox no tiene ese botón).
    /// </summary>
    private void ShowFriendlyError()
    {
        if (IsExiting || IsSessionEnding) return;
        if (Interlocked.Exchange(ref _errorDialogOpen, 1) == 1) return;

        try
        {
            if (_viewModel != null && _window is { IsLoaded: true })
            {
                _ = ShowFriendlyErrorInWindowAsync(_viewModel);
                return;
            }

            MessageBox.Show(
                $"Algo salió mal. Vuelve a intentarlo; si se repite, envía al desarrollador la carpeta:\n{AppPaths.LogsDir}",
                LauncherConfig.LauncherName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo mostrar el aviso de error.", ex);
        }
        Interlocked.Exchange(ref _errorDialogOpen, 0);
    }

    private async Task ShowFriendlyErrorInWindowAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.ShowUnexpectedErrorAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _errorDialogOpen, 0);
        }
    }

    /// <summary>Windows cierra sesión/apaga: sin diálogos y sin cancelar nunca; la descarga queda en pausa.</summary>
    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        IsSessionEnding = true;
        IsExiting = true;
        Log.Info($"Windows está cerrando la sesión ({e.ReasonSessionEnding}).");
        try
        {
            _viewModel?.CancelCurrentOperation("Cierre de sesión de Windows");
            _viewModel?.FlushSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Error al prepararse para el cierre de sesión.", ex);
        }
    }
}
