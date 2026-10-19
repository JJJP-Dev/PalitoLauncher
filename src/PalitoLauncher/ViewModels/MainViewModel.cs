using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Downloads;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Settings;
using PalitoLauncher.Core.State;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Helpers;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Páginas de la navegación lateral.</summary>
public enum AppPage
{
    Home,
    Versions,
    Roadmap,
    Settings,
}

/// <summary>Tipo de aviso secundario de la barra de acción.</summary>
public enum NoticeKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Operación larga en curso (una sola a la vez).</summary>
public enum OperationKind
{
    None,
    Install,
    Update,
    Repair,
    InstallOther,
    Uninstall,
    Move,
    LauncherUpdate,
}

/// <summary>Qué hacer si el usuario cierra la ventana.</summary>
public enum CloseDecision
{
    /// <summary>Se puede cerrar ya.</summary>
    Allow,

    /// <summary>Hay una operación cancelable: preguntar (la descarga queda en pausa).</summary>
    Confirm,

    /// <summary>Fase que no se puede interrumpir (instalando/limpiando): avisar y cerrar al terminar.</summary>
    MustWait,

    /// <summary>
    /// La encuesta (o «Reportar un problema») está abierta con algo sin enviar: preguntar «¿Salir sin enviarla?»
    /// (<see cref="MainViewModel.ConfirmCloseWithUnsentFeedbackAsync"/>) y después volver a decidir.
    /// </summary>
    ConfirmUnsentFeedback,
}

/// <summary>
/// ViewModel principal y único dueño de las instancias del Core. Mantiene el estado (instalación, releases, consulta,
/// operación en curso, juego abierto) y deriva de él, con LauncherStateLogic, todo lo que muestra la ventana.
/// Todo su estado se toca solo desde el hilo de UI; el trabajo pesado va a Task.Run o a métodos async del Core.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    internal const string GenericErrorMessage =
        "Algo salió mal. Vuelve a intentarlo; si se repite, pulsa «Crear informe» y envíaselo al desarrollador.";

    private const string BusyMessage = "Espera a que termine la operación en curso.";

    // Glifos de Segoe Fluent Icons / Segoe MDL2 Assets.
    private const string GlyphPlay = "";
    private const string GlyphDownload = "";
    private const string GlyphPause = "";
    private const string GlyphCancel = "";
    private const string GlyphRepair = "";
    private const string GlyphRetry = "";

    private static readonly string Game = LauncherConfig.GameDisplayName;

    private readonly Dispatcher _dispatcher;
    private readonly GitHubClient _github;
    private readonly GameInstaller _installer;
    private readonly GameProcessManager _process;
    private readonly SettingsStore _settingsStore;
    private readonly LauncherSelfUpdater _selfUpdater;
    private readonly SurveyProvider? _surveyProvider;
    private readonly FeedbackService? _feedback;
    private readonly DispatcherTimer _periodicTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _saveLock = new();

    // ───────────── Estado ─────────────
    private LauncherSettings _settings = new();
    private bool _settingsLoaded;
    private Task _saveChain = Task.CompletedTask;

    private bool _initialized;
    private string? _installRoot;
    private InstallStatus? _installStatus;
    private IReadOnlyList<GameRelease>? _gameReleases;
    private ReleaseFetchResult? _lastFetchResult;
    private DateTimeOffset? _lastSuccessfulFetchUtc;
    private DateTimeOffset? _lastForcedLauncherCheckUtc; // última comprobación del launcher pedida a mano («Buscar ahora»)
    private bool _fetchFailed;
    private string? _fetchErrorMessage;
    private bool _fetchErrorIsRateLimit;
    private bool _isFetching;
    private bool _initialFetchDone;
    private bool _playGateOpen;
    private bool _isBusy;
    private bool _isGameRunning;
    private InstallProgress? _currentProgress;
    private DownloadProgress? _launcherDownloadProgress;
    private long _pendingResumeBytes;
    private OperationKind _operationKind;
    private CancellationTokenSource? _operationCts;
    private Task? _operationTask;
    private bool _cancelRequested;
    private bool _closeWhenOperationEnds;
    private bool _primaryActionRunning;
    private string? _statusOverride;
    private bool _playWithoutUpdate;
    private bool _coopWarningShown;
    private bool _minimizedByUs;
    private string? _crashNotice;
    private string? _autoOperationError;
    private long? _autoUpdateSuppressedAssetId;
    private long? _lastAttentionAssetId;
    private LauncherUpdateInfo? _launcherUpdate;
    private string? _newBadgeTag;
    private long? _notesReleaseId;
    private string? _notesBody;
    private bool _stateLogicErrorLogged;
    private bool _reportRunning;
    private bool _disposed;

    // Acciones dinámicas de los enlaces/avisos (dependen del estado).
    private Func<Task>? _secondaryLinkAction;
    private Func<Task>? _tertiaryLinkAction;
    private Func<Task>? _noticeAction;
    private Func<Task>? _noticeAction2;
    private bool _primaryEnabled;

    // ───────────── Propiedades de presentación ─────────────
    private AppPage _currentPage = AppPage.Home;
    private LauncherState _state = LauncherState.Initializing;
    private string _statusText = "Cargando…";
    private bool _isProgressVisible;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private bool _isProgressPaused;
    private string? _secondaryNoticeText;
    private NoticeKind _secondaryNoticeKind;
    private string? _secondaryNoticeActionText;
    private string? _secondaryNoticeAction2Text;
    private bool _canDismissSecondaryNotice;
    private string _primaryButtonText = "CARGANDO…";
    private string? _primaryButtonGlyph;
    private string? _secondaryLinkText;
    private string? _tertiaryLinkText;
    private string _installedVersionText = "No instalada";
    private string _latestVersionText = "";
    private bool _hasLatest;
    private bool _isLatestNew;
    private bool _isOffline;
    private string _offlineChipText = "Sin conexión";
    private string _notesTitle = "";
    private FlowDocument? _notesDocument;
    private string _releasesInfoText = "";
    private bool _hasReleases;
    private string _releasesEmptyText = "Cargando versiones…";
    private bool _isLauncherUpdateVisible;
    private string _launcherUpdateText = "";
    private ImageSource? _backgroundImage;
    private string _toastText = "";
    private bool _isToastVisible;
    private TaskbarItemProgressState _taskbarProgressState = TaskbarItemProgressState.None;
    private double _taskbarProgressValue;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Instancias del Core (este ViewModel es su único dueño).
        var http = HttpFactory.Shared;
        _github = new GitHubClient(http, AppPaths.CacheDir);
        var downloader = new FileDownloader(http);
        _installer = new GameInstaller(downloader, LauncherConfig.GameExecutableName);
        _process = new GameProcessManager();
        _settingsStore = new SettingsStore(AppPaths.SettingsFile);
        _selfUpdater = new LauncherSelfUpdater(_github, new FileDownloader(http), AppPaths.CacheDir);
        // Opiniones: si algo falla al crearlos, el launcher sigue funcionando sin ellas.
        _surveyProvider = CreateSurveyProvider(_github);
        _feedback = CreateFeedbackService();
        // launcher.json (anuncios, sesiones de prueba…): igual, opcional.
        _manifestProvider = CreateManifestProvider(_github);
        // «Reportar un problema» (bandeja de salida en disco): igual, opcional.
        _bugReports = CreateBugReportService();
        // Misiones de prueba (launcher/missions.json + eventos del juego en Player.log): igual, opcional.
        _missionsProvider = CreateMissionsProvider(_github);
        _missionStore = CreateMissionStore();
        _missionReports = CreateMissionReportService();

        _process.GameStarted += OnGameStarted;
        _process.GameExited += OnGameExited;

        Dialogs = new DialogService();
        Dialogs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogService.IsOpen)) OnPropertyChanged(nameof(IsOverlayOpen));
        };

        Releases = new ObservableCollection<ReleaseItemViewModel>();
        LauncherVersionText = "v" + AppInfo.VersionText;

        NavigateCommand = new RelayCommand(Navigate);
        PrimaryCommand = new RelayCommand(ExecutePrimary, () => _primaryEnabled);
        SecondaryLinkCommand = new RelayCommand(() => RunAction(_secondaryLinkAction, "Enlace secundario"), () => _secondaryLinkAction != null);
        TertiaryLinkCommand = new RelayCommand(() => RunAction(_tertiaryLinkAction, "Enlace terciario"), () => _tertiaryLinkAction != null);
        SecondaryNoticeActionCommand = new RelayCommand(() => RunAction(_noticeAction, "Acción del aviso"), () => _noticeAction != null);
        SecondaryNoticeAction2Command = new RelayCommand(() => RunFlow(_noticeAction2 ?? (() => Task.CompletedTask), "Segunda acción del aviso"),
            () => _noticeAction2 != null);
        DismissSecondaryNoticeCommand = new RelayCommand(DismissSecondaryNotice, () => CanDismissSecondaryNotice);

        OpenGameFolderCommand = new RelayCommand(OpenGameFolder, () => _installRoot != null && _installStatus?.State is InstallState.Installed or InstallState.Broken);
        OpenInstallFolderCommand = new RelayCommand(OpenInstallFolder, () => _installRoot != null && _installStatus?.State is InstallState.Installed or InstallState.Broken);
        OpenSavesFolderCommand = new AsyncRelayCommand(OpenSavesFolderAsync, () => _installRoot != null && _installStatus?.State == InstallState.Installed);
        CheckForUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(includeLauncher: true), () => !_isFetching && _initialized);
        CheckNowCommand = CheckForUpdatesCommand;
        RefreshReleasesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(includeLauncher: false), () => !_isFetching && _initialized);
        RepairCommand = new RelayCommand(() => RunFlow(() => RepairAsync(confirm: true), "Reparar"), () => CanModifyInstallation);
        UninstallCommand = new RelayCommand(() => RunFlow(UninstallAsync, "Desinstalar"), () => CanModifyInstallation);
        MoveInstallCommand = new RelayCommand(() => RunFlow(MoveInstallAsync, "Mover"), () => CanModifyInstallation && _installStatus?.State == InstallState.Installed);
        ChooseAnotherFolderCommand = new RelayCommand(() => RunAction(ChooseAnotherFolderAsync, "Elegir otra carpeta"), () => CanLeaveMissingDrive);
        ForgetInstallFolderCommand = new RelayCommand(() => RunAction(ForgetInstallFolderAsync, "Olvidar carpeta"), () => CanLeaveMissingDrive);
        ClearDownloadsCommand = new RelayCommand(() => RunFlow(() => ClearDownloadsAsync(confirm: true), "Borrar descargas"),
            () => !IsBusy && _installRoot != null && _installStatus is { State: not InstallState.DriveMissing });
        CreateReportCommand = new AsyncRelayCommand(CreateReportAsync, () => !_reportRunning);
        OpenLatestOnGitHubCommand = new RelayCommand(() => ShellService.OpenUrl(Latest?.Release.HtmlUrl), () => ShellService.IsWebUrl(Latest?.Release.HtmlUrl));
        OpenGameReleasesPageCommand = new RelayCommand(() => ShellService.OpenUrl(LauncherConfig.GameReleasesPageUrl));
        UpdateLauncherCommand = new RelayCommand(() => RunFlow(UpdateLauncherAsync, "Actualizar launcher"), () => !IsBusy && _launcherUpdate != null);
        ViewLauncherChangesCommand = new RelayCommand(() =>
            ShellService.OpenUrl(ShellService.IsWebUrl(_launcherUpdate?.Release.HtmlUrl) ? _launcherUpdate!.Release.HtmlUrl : LauncherConfig.LauncherReleasesPageUrl));
        SkipLauncherUpdateCommand = new RelayCommand(SkipLauncherUpdate, () => _launcherUpdate != null);
        CreateDesktopShortcutCommand = new AsyncRelayCommand(CreateDesktopShortcutAsync);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        OpenLauncherGitHubCommand = new RelayCommand(() => ShellService.OpenUrl(LauncherConfig.LauncherRepoUrl));

        // Roadmap.
        RefreshRoadmapCommand = new AsyncRelayCommand(RefreshRoadmapManuallyAsync, () => !_roadmapLoading);
        OpenRoadmapOnGitHubCommand = new RelayCommand(OpenRoadmapOnGitHub);
        ShowRoadmapCommand = new RelayCommand(() => Navigate(AppPage.Roadmap));

        // Opiniones.
        SendFeedbackCommand = new RelayCommand(
            () => RunFlow(() => OpenSurveyAsync(FeedbackTrigger.Manual, null, null, null), "Enviar opinión"), () => CanOpenSurvey);
        OpenFeedbackFromPromptCommand = new RelayCommand(OpenFeedbackFromPrompt, _ => CanOpenSurvey);
        DismissFeedbackPromptCommand = new RelayCommand(DismissFeedbackPrompt);
        NeverAskFeedbackCommand = new RelayCommand(NeverAskFeedback);
        RetryFeedbackSendCommand = new AsyncRelayCommand(RetryFeedbackSendAsync, () => !_flushRunning && _feedback != null);

        // Reportes de problemas y herramientas (hostear, modo desarrollador).
        ReportProblemCommand = new RelayCommand(() => RunFlow(ReportProblemAsync, "Reportar un problema"), () => CanOpenBugReport);
        RetryBugReportSendCommand = new AsyncRelayCommand(RetryBugReportSendAsync, () => !_bugFlushRunning && _bugReports != null);
        ShowHostingCommand = new RelayCommand(() => RunFlow(ShowHostingAsync, "Hostear partida"), () => !_hostingOpen);
        LaunchExtraInstanceCommand = new RelayCommand(() => RunFlow(LaunchExtraInstanceAsync, "Abrir otra instancia del juego"),
            () => _settings.DeveloperMode && !IsBusy);

        SendMissionResultsCommand = new RelayCommand(() => RunFlow(SendMissionResultsNowAsync, "Enviar resultados de las misiones"),
            () => !_missionSendRunning && _missionState != null && _missionReports != null);

        Settings = new SettingsViewModel(this);

        // missions.json: tarjeta «Misiones de prueba».
        Missions = new MissionsCardViewModel(OnMissionAction, () => SendMissionResultsCommand.Execute(null),
            () => SendMissionResultsCommand.CanExecute(null));

        // launcher.json: tarjeta de la sesión de prueba.
        Playtest = new PlaytestCardViewModel(
            () => RunFlow(AddPlaytestToCalendarAsync, "Añadir la sesión al calendario"),
            JoinPlaytestDiscord,
            RunPlaytestVersionAction,
            CanRunPlaytestVersionAction);

        _periodicTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LauncherConfig.PeriodicCheckInterval };
        _periodicTimer.Tick += (_, _) => RunFlow(PeriodicCheckAsync, "Comprobación periódica", showErrors: false);

        // launcher.json se relee cada 15 min (raw: no gasta cupo) y el reloj recalcula anuncios y cuenta atrás cada 30 s.
        _manifestTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LauncherConfig.ManifestRefreshInterval };
        _manifestTimer.Tick += (_, _) =>
        {
            RunFlow(RefreshManifestAsync, "launcher.json periódico", showErrors: false);
            RunFlow(RefreshMissionsAsync, "missions.json periódico", showErrors: false);
        };
        _manifestClockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ManifestClockInterval };
        _manifestClockTimer.Tick += (_, _) => OnManifestClockTick();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        UpdateState();
    }

    // ───────────── Eventos para la ventana ─────────────

    /// <summary>Minimizar la ventana (tras lanzar el juego con AfterLaunch = Minimize).</summary>
    public event EventHandler? MinimizeRequested;

    /// <summary>Restaurar y traer al frente (al cerrarse el juego si la minimizamos nosotros).</summary>
    public event EventHandler? RestoreRequested;

    /// <summary>Hay algo nuevo (actualización): hacer parpadear la barra de tareas si la ventana no está activa.</summary>
    public event EventHandler? AttentionRequested;

    /// <summary>Cerrar la ventana sin preguntar (AfterLaunch = Close, o al terminar la operación tras pedir cerrar).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Cerrar la aplicación de inmediato (la versión nueva del launcher ya arrancó).</summary>
    public event EventHandler? ShutdownRequested;

    // ───────────── Hijos / servicios ─────────────

    public DialogService Dialogs { get; }
    public SettingsViewModel Settings { get; }
    public ObservableCollection<ReleaseItemViewModel> Releases { get; }

    /// <summary>Ajustes vivos (solo hilo de UI). Los modifica SettingsViewModel.</summary>
    internal LauncherSettings CurrentSettings => _settings;

    // ───────────── Navegación ─────────────

    public AppPage CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!SetProperty(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(IsHomePage));
            OnPropertyChanged(nameof(IsVersionsPage));
            OnPropertyChanged(nameof(IsRoadmapPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }

    public bool IsHomePage => _currentPage == AppPage.Home;
    public bool IsVersionsPage => _currentPage == AppPage.Versions;
    public bool IsSettingsPage => _currentPage == AppPage.Settings;

    // ───────────── Estado general ─────────────

    public LauncherState State { get => _state; private set => SetProperty(ref _state, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set => SetProperty(ref _isGameRunning, value);
    }

    public bool IsOverlayOpen => Dialogs.IsOpen;

    public string LauncherVersionText { get; }

    public string GameName => Game;

    // ───────────── Barra de acción ─────────────

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsProgressVisible { get => _isProgressVisible; private set => SetProperty(ref _isProgressVisible, value); }

    /// <summary>0..100.</summary>
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    public bool IsProgressPaused { get => _isProgressPaused; private set => SetProperty(ref _isProgressPaused, value); }

    public string? SecondaryNoticeText
    {
        get => _secondaryNoticeText;
        private set
        {
            if (SetProperty(ref _secondaryNoticeText, value)) OnPropertyChanged(nameof(HasSecondaryNotice));
        }
    }

    public bool HasSecondaryNotice => !string.IsNullOrWhiteSpace(_secondaryNoticeText);
    public NoticeKind SecondaryNoticeKind { get => _secondaryNoticeKind; private set => SetProperty(ref _secondaryNoticeKind, value); }

    public string? SecondaryNoticeActionText
    {
        get => _secondaryNoticeActionText;
        private set
        {
            if (SetProperty(ref _secondaryNoticeActionText, value)) OnPropertyChanged(nameof(HasSecondaryNoticeAction));
        }
    }

    public bool HasSecondaryNoticeAction => !string.IsNullOrWhiteSpace(_secondaryNoticeActionText);

    /// <summary>Segunda acción del aviso (p.ej. «Contarnos qué pasó» en el aviso de cierre inesperado).</summary>
    public string? SecondaryNoticeAction2Text
    {
        get => _secondaryNoticeAction2Text;
        private set
        {
            if (SetProperty(ref _secondaryNoticeAction2Text, value)) OnPropertyChanged(nameof(HasSecondaryNoticeAction2));
        }
    }

    public bool HasSecondaryNoticeAction2 => !string.IsNullOrWhiteSpace(_secondaryNoticeAction2Text);

    public bool CanDismissSecondaryNotice { get => _canDismissSecondaryNotice; private set => SetProperty(ref _canDismissSecondaryNotice, value); }

    public string PrimaryButtonText { get => _primaryButtonText; private set => SetProperty(ref _primaryButtonText, value); }
    public string? PrimaryButtonGlyph { get => _primaryButtonGlyph; private set => SetProperty(ref _primaryButtonGlyph, value); }

    public string? SecondaryLinkText
    {
        get => _secondaryLinkText;
        private set
        {
            if (SetProperty(ref _secondaryLinkText, value)) OnPropertyChanged(nameof(HasSecondaryLink));
        }
    }

    public bool HasSecondaryLink => !string.IsNullOrWhiteSpace(_secondaryLinkText);

    public string? TertiaryLinkText
    {
        get => _tertiaryLinkText;
        private set
        {
            if (SetProperty(ref _tertiaryLinkText, value)) OnPropertyChanged(nameof(HasTertiaryLink));
        }
    }

    public bool HasTertiaryLink => !string.IsNullOrWhiteSpace(_tertiaryLinkText);

    // ───────────── Inicio ─────────────

    public string InstalledVersionText { get => _installedVersionText; private set => SetProperty(ref _installedVersionText, value); }
    public string LatestVersionText { get => _latestVersionText; private set => SetProperty(ref _latestVersionText, value); }
    public bool HasLatest { get => _hasLatest; private set => SetProperty(ref _hasLatest, value); }
    public bool IsLatestNew { get => _isLatestNew; private set => SetProperty(ref _isLatestNew, value); }
    public bool IsOffline { get => _isOffline; private set => SetProperty(ref _isOffline, value); }
    public string OfflineChipText { get => _offlineChipText; private set => SetProperty(ref _offlineChipText, value); }
    public string NotesTitle { get => _notesTitle; private set => SetProperty(ref _notesTitle, value); }
    public FlowDocument? NotesDocument { get => _notesDocument; private set => SetProperty(ref _notesDocument, value); }

    // ───────────── Versiones ─────────────

    public string ReleasesInfoText { get => _releasesInfoText; private set => SetProperty(ref _releasesInfoText, value); }
    public bool HasReleases { get => _hasReleases; private set => SetProperty(ref _hasReleases, value); }
    public string ReleasesEmptyText { get => _releasesEmptyText; private set => SetProperty(ref _releasesEmptyText, value); }

    // ───────────── Banner del launcher ─────────────

    public bool IsLauncherUpdateVisible { get => _isLauncherUpdateVisible; private set => SetProperty(ref _isLauncherUpdateVisible, value); }
    public string LauncherUpdateText { get => _launcherUpdateText; private set => SetProperty(ref _launcherUpdateText, value); }

    // ───────────── Fondo, toast y barra de tareas ─────────────

    public ImageSource? BackgroundImage
    {
        get => _backgroundImage;
        private set
        {
            if (SetProperty(ref _backgroundImage, value)) OnPropertyChanged(nameof(HasBackgroundImage));
        }
    }

    public bool HasBackgroundImage => _backgroundImage != null;

    public string ToastText { get => _toastText; private set => SetProperty(ref _toastText, value); }
    public bool IsToastVisible { get => _isToastVisible; private set => SetProperty(ref _isToastVisible, value); }

    public TaskbarItemProgressState TaskbarProgressState { get => _taskbarProgressState; private set => SetProperty(ref _taskbarProgressState, value); }

    /// <summary>0..1.</summary>
    public double TaskbarProgressValue { get => _taskbarProgressValue; private set => SetProperty(ref _taskbarProgressValue, value); }

    // ───────────── Comandos ─────────────

    public ICommand NavigateCommand { get; }
    public ICommand PrimaryCommand { get; }
    public ICommand SecondaryLinkCommand { get; }
    public ICommand TertiaryLinkCommand { get; }
    public ICommand SecondaryNoticeActionCommand { get; }
    public ICommand SecondaryNoticeAction2Command { get; }
    public ICommand DismissSecondaryNoticeCommand { get; }
    public ICommand OpenGameFolderCommand { get; }
    public ICommand OpenInstallFolderCommand { get; }
    public ICommand OpenSavesFolderCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand CheckNowCommand { get; }
    public ICommand RefreshReleasesCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand MoveInstallCommand { get; }

    /// <summary>Unidad no disponible: instalar el juego en otra carpeta (abre el diálogo de instalación).</summary>
    public ICommand ChooseAnotherFolderCommand { get; }

    /// <summary>Unidad no disponible: dejar de usar esa carpeta (no borra nada) y volver a "No instalado".</summary>
    public ICommand ForgetInstallFolderCommand { get; }

    public ICommand ClearDownloadsCommand { get; }
    public ICommand CreateReportCommand { get; }
    public ICommand OpenLatestOnGitHubCommand { get; }
    public ICommand OpenGameReleasesPageCommand { get; }
    public ICommand UpdateLauncherCommand { get; }
    public ICommand ViewLauncherChangesCommand { get; }
    public ICommand SkipLauncherUpdateCommand { get; }
    public ICommand CreateDesktopShortcutCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }
    public ICommand OpenLauncherGitHubCommand { get; }

    // ───────────── Ayudantes de estado ─────────────

    private GameRelease? Latest => _gameReleases is { Count: > 0 } list ? list[0] : null;

    private bool CanModifyInstallation =>
        !IsBusy && _installRoot != null && _installStatus?.State is InstallState.Installed or InstallState.Broken;

    /// <summary>Salidas del estado DriveMissing ("Elegir otra carpeta…" / "Olvidar esta carpeta").</summary>
    private bool CanLeaveMissingDrive =>
        _initialized && !IsBusy && !_primaryActionRunning && _installRoot != null && _installStatus?.State == InstallState.DriveMissing;

    private string GameExecutableName =>
        _installStatus?.Info?.ExecutableName is { Length: > 0 } exe ? exe : LauncherConfig.GameExecutableName;

    private static string DisplayOf(InstalledGameInfo info)
        => !string.IsNullOrWhiteSpace(info.VersionDisplay) ? info.VersionDisplay : info.Tag;

    private static string DriveOf(string? path)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(path) ? null : Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? (path ?? "") : root;
        }
        catch (Exception)
        {
            return path ?? "";
        }
    }

    /// <summary>Aviso del estado DriveMissing (coincide con las salidas que existen: REINTENTAR y "Elegir otra carpeta…").</summary>
    private string DriveMissingMessage =>
        $"No se encuentra la unidad {DriveOf(_installRoot)}. Conéctala y pulsa REINTENTAR, o elige otra carpeta para instalar el juego.";

    private void Navigate(object? parameter)
    {
        var page = parameter switch
        {
            AppPage p => p,
            string s when Enum.TryParse<AppPage>(s, ignoreCase: true, out var parsed) => parsed,
            _ => _currentPage,
        };

        if (page == _currentPage) return;
        if (_currentPage == AppPage.Home) _newBadgeTag = null; // al salir de Inicio, "Nuevo" deja de mostrarse
        CurrentPage = page;

        if (page == AppPage.Settings)
        {
            Settings.RefreshInstallInfo(_installRoot, _installStatus);
            RunFlow(RefreshPendingFeedbackAsync, "Opiniones pendientes", showErrors: false);
            RunFlow(RefreshPendingBugReportsAsync, "Reportes pendientes", showErrors: false);
        }
        else if (page == AppPage.Roadmap)
        {
            RefreshRoadmapIfStale();
        }
        UpdateState();
    }

    /// <summary>
    /// Recalcula el estado con LauncherStateLogic y todo lo que depende de él (botón principal, textos, progreso,
    /// avisos, chips, barra de tareas). Barato: se llama tras cualquier cambio, también con cada tick de progreso.
    /// </summary>
    private void UpdateState()
    {
        if (_disposed) return;

        var latest = Latest;
        var status = _installStatus;
        var info = status?.Info;
        var state = ComputeStateSafe();
        State = state;

        bool hasPending = !IsBusy && latest != null && _installRoot != null && status?.HasPendingDownload == true
                          && _pendingResumeBytes > 0 && state is LauncherState.NotInstalled or LauncherState.UpdateAvailable;
        double pendingFraction = hasPending && latest!.Asset.Size > 0
            ? Math.Clamp((double)_pendingResumeBytes / latest.Asset.Size, 0, 1)
            : 0;

        string primaryText;
        string? glyph = null;
        bool primaryEnabled = true;
        string statusText;
        double? progressFraction = null;
        bool progressVisible = false;
        bool progressPaused = false;
        string? linkText = null;
        Func<Task>? linkAction = null;
        string? link2Text = null;
        Func<Task>? link2Action = null;
        (NoticeKind Kind, string Text)? stateNotice = null;

        switch (state)
        {
            case LauncherState.Busy:
                ComputeBusyPresentation(out primaryText, out glyph, out primaryEnabled, out statusText, out progressFraction);
                progressVisible = true;
                break;

            case LauncherState.GameRunning:
                primaryText = "EN EJECUCIÓN";
                primaryEnabled = false;
                statusText = info != null ? $"{Game} está abierto · {DisplayOf(info)}" : $"{Game} está abierto";
                linkText = "¿El juego no responde? Forzar cierre";
                linkAction = ForceCloseGameAsync;
                break;

            case LauncherState.Initializing:
                primaryText = "CARGANDO…";
                primaryEnabled = false;
                statusText = "Cargando…";
                break;

            case LauncherState.DriveMissing:
                primaryText = "REINTENTAR";
                glyph = GlyphRetry;
                statusText = "No se encuentra la carpeta del juego";
                stateNotice = (NoticeKind.Warning, DriveMissingMessage);
                // Salida real del estado: instalar en otra carpeta (nunca se hace solo). También en Ajustes › Instalación.
                linkText = "Elegir otra carpeta…";
                linkAction = ChooseAnotherFolderAsync;
                break;

            case LauncherState.LoadFailed:
                primaryText = "REINTENTAR";
                glyph = GlyphRetry;
                statusText = "No se pudo obtener la lista de versiones";
                stateNotice = (NoticeKind.Error, _fetchErrorMessage ?? "No se pudo conectar con GitHub.");
                if (_fetchErrorIsRateLimit)
                {
                    linkText = "Abrir releases en GitHub";
                    linkAction = OpenGameReleasesPageAsync;
                }
                break;

            case LauncherState.NoReleases:
                primaryText = "SIN VERSIONES";
                primaryEnabled = false;
                statusText = "Aún no hay builds publicadas en GitHub.";
                break;

            case LauncherState.NotInstalled:
                if (hasPending)
                {
                    primaryText = $"CONTINUAR ({UiFormat.Percent(pendingFraction)} %)";
                    statusText = $"En pausa · {UiFormat.Bytes(_pendingResumeBytes)} de {UiFormat.Bytes(latest!.Asset.Size)}";
                    progressVisible = progressPaused = true;
                    progressFraction = pendingFraction;
                    link2Text = "Descartar descarga";
                    link2Action = () => ClearDownloadsAsync(confirm: true);
                }
                else
                {
                    primaryText = "INSTALAR";
                    statusText = latest != null
                        ? $"{Game} no está instalado · Última versión: {latest.VersionDisplay}"
                        : $"{Game} no está instalado";
                }
                glyph = GlyphDownload;
                break;

            case LauncherState.UpdateAvailable:
                if (hasPending)
                {
                    primaryText = $"CONTINUAR ({UiFormat.Percent(pendingFraction)} %)";
                    statusText = $"En pausa · {UiFormat.Bytes(_pendingResumeBytes)} de {UiFormat.Bytes(latest!.Asset.Size)}";
                    progressVisible = progressPaused = true;
                    progressFraction = pendingFraction;
                    link2Text = "Descartar descarga";
                    link2Action = () => ClearDownloadsAsync(confirm: true);
                }
                else
                {
                    primaryText = "ACTUALIZAR";
                    statusText = BuildUpdateStatusText(info, latest);
                }
                glyph = GlyphDownload;
                linkText = "Jugar sin actualizar";
                linkAction = PlayWithoutUpdateAsync;
                break;

            case LauncherState.Broken:
                primaryText = "REPARAR";
                glyph = GlyphRepair;
                statusText = status?.HasForeignContent == true
                    ? "La carpeta del juego tiene archivos que no son del launcher. Pulsa REPARAR (se guardarán aparte)."
                    : "La instalación está dañada o incompleta. Pulsa REPARAR.";
                break;

            default: // Ready
                primaryText = "JUGAR";
                glyph = GlyphPlay;
                primaryEnabled = _playGateOpen;
                statusText = info != null ? $"Versión instalada: {DisplayOf(info)}" : "Listo para jugar";
                break;
        }

        if (state != LauncherState.Busy)
        {
            if (_statusOverride != null) statusText = _statusOverride;
            if (_primaryActionRunning) primaryEnabled = false;
        }
        else if (_cancelRequested)
        {
            primaryText = "DETENIENDO…";
            glyph = null;
            primaryEnabled = false;
        }

        PrimaryButtonText = primaryText;
        PrimaryButtonGlyph = glyph;
        _primaryEnabled = primaryEnabled;
        StatusText = statusText;

        IsProgressVisible = progressVisible;
        IsProgressPaused = progressPaused;
        IsProgressIndeterminate = progressVisible && progressFraction == null;
        ProgressValue = Math.Round((progressFraction ?? 0) * 100, 2);

        _secondaryLinkAction = linkAction;
        SecondaryLinkText = linkText;
        _tertiaryLinkAction = link2Action;
        TertiaryLinkText = link2Text;

        UpdateNotice(state, stateNotice, status);

        // Barra de tareas.
        if (state == LauncherState.Busy)
        {
            TaskbarProgressState = progressFraction == null ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
            TaskbarProgressValue = progressFraction ?? 0;
        }
        else if (hasPending)
        {
            TaskbarProgressState = TaskbarItemProgressState.Paused;
            TaskbarProgressValue = pendingFraction;
        }
        else
        {
            TaskbarProgressState = TaskbarItemProgressState.None;
            TaskbarProgressValue = 0;
        }

        UpdateHome(latest, status, info);
        UpdateReleasesInfo();

        // Aviso de actualización nueva (parpadeo si la ventana no está activa).
        if (state == LauncherState.UpdateAvailable && latest != null && _lastAttentionAssetId != latest.Asset.Id)
        {
            _lastAttentionAssetId = latest.Asset.Id;
            AttentionRequested?.Invoke(this, EventArgs.Empty);
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private LauncherState ComputeStateSafe()
    {
        try
        {
            return LauncherStateLogic.ComputeState(new LauncherStateInput
            {
                Install = _installStatus,
                Releases = _gameReleases,
                FetchFailed = _fetchFailed,
                IsBusy = IsBusy,
                IsGameRunning = IsGameRunning,
            });
        }
        catch (Exception ex)
        {
            if (!_stateLogicErrorLogged)
            {
                _stateLogicErrorLogged = true;
                Log.Error("Error al calcular el estado del launcher.", ex);
            }
            if (IsBusy) return LauncherState.Busy;
            return _installStatus == null ? LauncherState.Initializing : LauncherState.LoadFailed;
        }
    }

    private static UpdateKind GetUpdateKindSafe(InstalledGameInfo? info, GameRelease? latest)
    {
        try
        {
            return LauncherStateLogic.GetUpdateKind(info, latest);
        }
        catch (Exception ex)
        {
            Log.Error("Error al comparar versiones.", ex);
            return UpdateKind.None;
        }
    }

    private static string BuildUpdateStatusText(InstalledGameInfo? info, GameRelease? latest)
    {
        if (latest == null) return "Hay una actualización disponible";
        if (GetUpdateKindSafe(info, latest) == UpdateKind.Revision)
        {
            var when = UiFormat.ShortDateTime(latest.Asset.UpdatedAt ?? latest.Release.PublishedAt);
            return when.Length > 0
                ? $"Hay una revisión nueva de {latest.VersionDisplay} (subida el {when})"
                : $"Hay una revisión nueva de {latest.VersionDisplay}";
        }
        return info != null
            ? $"Nueva versión disponible: {latest.VersionDisplay} (tienes {DisplayOf(info)})"
            : $"Nueva versión disponible: {latest.VersionDisplay}";
    }

    private void ComputeBusyPresentation(out string primaryText, out string? glyph, out bool enabled, out string status,
        out double? fraction)
    {
        if (_operationKind == OperationKind.LauncherUpdate)
        {
            var d = _launcherDownloadProgress;
            fraction = d?.Fraction;
            bool downloading = d == null || (d.Value.Fraction ?? 0) < 1;
            if (d is { } p)
            {
                var amount = p.TotalBytes is long total and > 0
                    ? $"{UiFormat.Bytes(p.BytesReceived)} de {UiFormat.Bytes(total)}"
                    : UiFormat.Bytes(p.BytesReceived);
                status = downloading
                    ? $"Descargando la nueva versión del launcher… {UiFormat.Percent(p.Fraction)} % · {amount}"
                    : "Instalando la nueva versión del launcher…";
            }
            else
            {
                status = "Preparando la actualización del launcher…";
            }
            primaryText = downloading ? "CANCELAR" : "ACTUALIZANDO…";
            glyph = downloading ? GlyphCancel : null;
            enabled = downloading;
            return;
        }

        var progress = _currentProgress ?? new InstallProgress(InstallPhase.Preparing, null, "Preparando…");
        fraction = progress.Fraction ?? progress.Download?.Fraction;
        status = FormatInstallStatus(progress);

        if (progress.Phase == InstallPhase.Downloading && _operationKind is not OperationKind.Move and not OperationKind.Uninstall)
        {
            primaryText = "PAUSAR";
            glyph = GlyphPause;
            enabled = true;
        }
        else if (progress.CanCancel)
        {
            primaryText = "CANCELAR";
            glyph = GlyphCancel;
            enabled = true;
        }
        else
        {
            primaryText = progress.Phase switch
            {
                InstallPhase.Installing => "INSTALANDO…",
                InstallPhase.CleaningUp => "LIMPIANDO…",
                InstallPhase.Uninstalling => "DESINSTALANDO…",
                InstallPhase.Verifying => "VERIFICANDO…",
                InstallPhase.Extracting => "EXTRAYENDO…",
                InstallPhase.Moving => "MOVIENDO…",
                InstallPhase.WaitingForGameExit => "ESPERANDO…",
                _ => "PREPARANDO…",
            };
            glyph = null;
            enabled = false;
        }
    }

    /// <summary>"Descargando… 45 % · 34.2 MB de 70.8 MB · 5.3 MB/s · 12 s" o el texto de la fase del instalador.</summary>
    private static string FormatInstallStatus(InstallProgress progress)
    {
        if (progress.Phase == InstallPhase.Downloading && progress.Download is { } d
            && (string.IsNullOrWhiteSpace(progress.Status) || progress.Status.StartsWith("Descargando", StringComparison.OrdinalIgnoreCase)))
        {
            var percent = d.Fraction is double f ? $"{UiFormat.Percent(f)} % · " : "";
            var amount = d.TotalBytes is long total and > 0
                ? $"{UiFormat.Bytes(d.BytesReceived)} de {UiFormat.Bytes(total)}"
                : UiFormat.Bytes(d.BytesReceived);
            var eta = d.Eta is TimeSpan e && e > TimeSpan.Zero ? UiFormat.Duration(e) : "—";
            return $"Descargando… {percent}{amount} · {UiFormat.Speed(d.BytesPerSecond)} · {eta}";
        }

        if (!string.IsNullOrWhiteSpace(progress.Status)) return progress.Status;
        return progress.Phase switch
        {
            InstallPhase.Downloading => "Descargando…",
            InstallPhase.Verifying => "Verificando archivo…",
            InstallPhase.Extracting => "Extrayendo…",
            InstallPhase.WaitingForGameExit => $"Cierra {Game} para terminar la actualización…",
            InstallPhase.Installing => "Instalando…",
            InstallPhase.CleaningUp => "Limpiando…",
            InstallPhase.Moving => "Moviendo…",
            InstallPhase.Uninstalling => "Desinstalando…",
            _ => "Preparando…",
        };
    }

    private void UpdateNotice(LauncherState state, (NoticeKind Kind, string Text)? stateNotice, InstallStatus? status)
    {
        string? text = null;
        var kind = NoticeKind.Info;
        string? actionText = null;
        Func<Task>? action = null;
        string? action2Text = null;
        Func<Task>? action2 = null;
        bool dismissible = false;

        if (_crashNotice != null)
        {
            text = _crashNotice;
            kind = NoticeKind.Error;
            // Reporte con los registros de esa partida (categoría «Cierre del juego»). Se abre aparte: el diálogo puede
            // estar abierto un rato y no debe bloquear el botón principal. «Crear informe» sigue en el menú ⋯.
            actionText = "Reportar el cierre";
            action = () =>
            {
                RunFlow(ReportCrashAsync, "Reportar el cierre");
                return Task.CompletedTask;
            };
            // En vez de la invitación tras la partida: contar qué pasó con la encuesta.
            action2Text = "Contarnos qué pasó";
            action2 = TellWhatHappenedAsync;
            dismissible = true;
        }
        else if (_autoOperationError != null)
        {
            text = _autoOperationError;
            kind = NoticeKind.Error;
            dismissible = true;
        }
        else if (stateNotice is { } sn)
        {
            text = sn.Text;
            kind = sn.Kind;
        }
        else if (_lastFetchResult?.IsStale == true)
        {
            text = string.IsNullOrWhiteSpace(_lastFetchResult.Warning)
                ? "Sin conexión con GitHub. Mostrando datos guardados."
                : _lastFetchResult.Warning;
            kind = NoticeKind.Warning;
        }
        else if (_fetchErrorMessage != null)
        {
            text = _fetchErrorMessage;
            kind = NoticeKind.Warning;
            if (_fetchErrorIsRateLimit)
            {
                actionText = "Abrir releases en GitHub";
                action = OpenGameReleasesPageAsync;
            }
        }
        else if (!_initialFetchDone && status?.State == InstallState.Installed && state != LauncherState.Busy)
        {
            text = "Comprobando actualizaciones…";
            kind = NoticeKind.Info;
        }

        _noticeAction = action;
        _noticeAction2 = action2;
        SecondaryNoticeKind = kind;
        SecondaryNoticeText = text;
        SecondaryNoticeActionText = action != null ? actionText : null;
        SecondaryNoticeAction2Text = action2 != null ? action2Text : null;
        CanDismissSecondaryNotice = dismissible;
    }

    private void DismissSecondaryNotice()
    {
        if (_crashNotice != null) _crashNotice = null;
        else _autoOperationError = null;
        UpdateState();
    }

    private void UpdateHome(GameRelease? latest, InstallStatus? status, InstalledGameInfo? info)
    {
        InstalledVersionText = info != null
            ? $"Instalada: {DisplayOf(info)}"
            : status?.State switch
            {
                InstallState.Broken => "Instalación dañada",
                InstallState.DriveMissing => "Unidad no disponible",
                _ => "No instalada",
            };

        HasLatest = latest != null;
        LatestVersionText = latest != null ? $"Última: {latest.VersionDisplay}" : "";

        bool rateLimited = _fetchErrorIsRateLimit || IsRateLimitedSafe();
        IsOffline = _lastFetchResult?.IsStale == true || _fetchErrorMessage != null;
        OfflineChipText = rateLimited ? "Datos guardados" : "Sin conexión";

        // Notas de la última versión (solo se regeneran si cambian). Con una actualización que se salta 2 o más versiones,
        // la tarjeta pasa a «Qué ha cambiado desde tu versión» con las notas de cada una (UpdateMultiVersionNotes).
        if (latest == null)
        {
            if (_notesReleaseId != null)
            {
                _notesReleaseId = null;
                _notesBody = null;
                NotesDocument = null;
            }
            IsMultiVersionNotes = false;
            NotesTitle = "";
        }
        else
        {
            if (!UpdateMultiVersionNotes(status?.State == InstallState.Installed ? info : null, latest))
            {
                var date = UiFormat.Date(latest.Release.PublishedAt ?? latest.Asset.UpdatedAt);
                NotesTitle = date.Length > 0 ? $"Novedades de {latest.VersionDisplay} — {date}" : $"Novedades de {latest.VersionDisplay}";
            }
            if (_notesReleaseId != latest.Release.Id || !string.Equals(_notesBody, latest.Release.Body, StringComparison.Ordinal))
            {
                _notesReleaseId = latest.Release.Id;
                _notesBody = latest.Release.Body;
                NotesDocument = MarkdownRenderer.Render(latest.Release.Body);
            }
        }

        // Badge "Nuevo": si la última no es la vista, se marca como vista al estar en Inicio (se ve durante esta visita).
        if (latest != null && _settingsLoaded && IsHomePage
            && !string.Equals(latest.Release.TagName, _settings.LastSeenReleaseTag, StringComparison.Ordinal))
        {
            _newBadgeTag = latest.Release.TagName;
            _settings.LastSeenReleaseTag = latest.Release.TagName;
            _ = SaveSettingsAsync();
        }
        IsLatestNew = latest != null && string.Equals(_newBadgeTag, latest.Release.TagName, StringComparison.Ordinal);
    }

    private void UpdateReleasesInfo()
    {
        if (_isFetching && _gameReleases == null)
            ReleasesInfoText = "Consultando GitHub…";
        else if (_lastFetchResult?.IsStale == true)
            ReleasesInfoText = string.IsNullOrWhiteSpace(_lastFetchResult.Warning)
                ? "Sin conexión con GitHub. Mostrando datos guardados."
                : _lastFetchResult.Warning!;
        else if (_fetchErrorMessage != null)
            ReleasesInfoText = _fetchErrorMessage;
        else if (_lastSuccessfulFetchUtc is { } t)
            ReleasesInfoText = $"Comprobado a las {UiFormat.Time(t)}";
        else
            ReleasesInfoText = "";

        ReleasesEmptyText = _gameReleases == null
            ? (_fetchFailed || _fetchErrorMessage != null ? _fetchErrorMessage ?? "No se pudo obtener la lista de versiones." : "Cargando versiones…")
            : "Aún no hay builds publicadas en GitHub.";
    }

    /// <summary>
    /// Reconstruye la lista de la página Versiones (conserva las expandidas) y, como cambió la lista o la instalación, lo
    /// que depende de ellas en launcher.json (badges «Con problemas», aviso de build con problemas, versión de la sesión).
    /// </summary>
    private void RebuildReleaseItems()
    {
        var releases = _gameReleases ?? Array.Empty<GameRelease>();
        var expanded = Releases.Where(r => r.IsExpanded).Select(r => r.Release.Asset.Id).ToHashSet();
        var latest = Latest;
        var installed = FindInstalledRelease(_installStatus?.Info, releases);

        Releases.Clear();
        foreach (var release in releases)
        {
            var item = new ReleaseItemViewModel(release, ReferenceEquals(release, installed), ReferenceEquals(release, latest),
                InstallSpecificReleaseAsync, () => !IsBusy && !_primaryActionRunning && _initialized,
                blockedReason: FindBlockSafe(release)?.Reason);
            if (expanded.Contains(release.Asset.Id)) item.IsExpanded = true;
            Releases.Add(item);
        }
        HasReleases = Releases.Count > 0;

        RefreshManifestPresentation();
    }

    private static GameRelease? FindInstalledRelease(InstalledGameInfo? info, IReadOnlyList<GameRelease> releases)
    {
        if (info == null) return null;
        var byAsset = releases.FirstOrDefault(r => info.AssetId != 0 && r.Asset.Id == info.AssetId);
        if (byAsset != null) return byAsset;
        var byHash = releases.FirstOrDefault(r => !string.IsNullOrWhiteSpace(info.Sha256)
            && string.Equals(r.Asset.Sha256, info.Sha256, StringComparison.OrdinalIgnoreCase));
        if (byHash != null) return byHash;
        return releases.FirstOrDefault(r => !string.IsNullOrEmpty(info.Tag) && string.Equals(r.Release.TagName, info.Tag, StringComparison.Ordinal));
    }

    private bool IsRateLimitedSafe()
    {
        try
        {
            return _github.IsRateLimited;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer el estado del límite de GitHub.", ex);
            return false;
        }
    }

    private int? RateLimitRemainingSafe()
    {
        try
        {
            return _github.RateLimitRemaining;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer el cupo restante de GitHub.", ex);
            return null;
        }
    }

    // ───────────── Toast ─────────────

    /// <summary>Mensaje breve abajo a la derecha (3–4 s según la longitud).</summary>
    internal void ShowToast(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed) return;
        ToastText = text;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(3 + text.Length / 60.0, 3, 4));
        _toastTimer.Start();
    }

    // ───────────── Ajustes ─────────────

    /// <summary>
    /// Guarda una copia de los ajustes en segundo plano, en orden. Nunca lanza. No guarda nada hasta haberlos cargado
    /// (para no pisar el archivo del usuario con valores por defecto).
    /// </summary>
    internal Task SaveSettingsAsync() => SaveSettingsCheckedAsync();

    /// <summary>
    /// Como <see cref="SaveSettingsAsync"/>, pero indica si se guardó de verdad (false si no se pudo escribir o aún no se
    /// han cargado). Para cambios que no pueden perderse (p.ej. la carpeta nueva al mover). Nunca lanza.
    /// </summary>
    internal Task<bool> SaveSettingsCheckedAsync()
    {
        if (!_settingsLoaded) return Task.FromResult(false);
        var snapshot = _settings.Clone();
        lock (_saveLock)
        {
            var task = _saveChain.ContinueWith(_ => SaveSnapshot(snapshot), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            _saveChain = task;
            return task;
        }
    }

    private bool SaveSnapshot(LauncherSettings snapshot)
    {
        try
        {
            if (_settingsStore.Save(snapshot)) return true;
            Log.Warn("No se pudieron guardar los ajustes.");
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al guardar los ajustes.", ex);
        }
        return false;
    }

    /// <summary>Espera (como mucho unos segundos) a que terminen los guardados pendientes. Para el cierre.</summary>
    internal void FlushSettings()
    {
        try
        {
            Settings.CommitLaunchArguments();
            Settings.CommitFeedbackNickname();
            Task chain;
            lock (_saveLock) chain = _saveChain;
            if (!chain.Wait(TimeSpan.FromSeconds(3))) Log.Warn("Los ajustes tardaron demasiado en guardarse al cerrar.");
        }
        catch (Exception ex)
        {
            Log.Error("Error al guardar los ajustes al cerrar.", ex);
        }
    }

    /// <summary>Lo llama SettingsViewModel tras cambiar un ajuste: guarda y reacciona.</summary>
    internal void OnSettingsChanged(SettingChange change)
    {
        _ = SaveSettingsAsync();
        switch (change)
        {
            case SettingChange.IncludePrereleases:
                RunFlow(ReselectReleasesAsync, "Cambiar versiones de prueba", showErrors: false);
                break;
            case SettingChange.AutoDownload:
                RunFlow(EvaluateAutoUpdateAsync, "Activar actualización automática", showErrors: false);
                break;
            case SettingChange.CheckLauncherUpdates:
                if (_settings.CheckLauncherUpdates)
                {
                    RunFlow(() => CheckLauncherUpdateAsync(force: false), "Buscar actualización del launcher", showErrors: false);
                }
                else
                {
                    _launcherUpdate = null;
                    IsLauncherUpdateVisible = false;
                }
                break;
            case SettingChange.PlaytestReminders:
                // Al activarlo con una sesión a punto de empezar, se avisa ya.
                if (_settings.PlaytestRemindersEnabled) CheckPlaytestReminder();
                break;
            case SettingChange.Missions:
                RefreshMissionsCard();
                break;
            case SettingChange.DeveloperMode:
                // Muestra u oculta «Abrir otra instancia» (junto a JUGAR y en el menú ⋯).
                OnPropertyChanged(nameof(IsDeveloperMode));
                Log.Info(_settings.DeveloperMode ? "Modo desarrollador activado." : "Modo desarrollador desactivado.");
                break;
        }
        UpdateState();
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            _periodicTimer.Stop();
            _toastTimer.Stop();
            _manifestTimer.Stop();
            _manifestClockTimer.Stop();
            _missionSendTimer?.Stop();
            _operationCts?.Cancel();
            _lifetimeCts.Cancel();
        }
        catch (Exception ex)
        {
            Log.Warn("Error al detener temporizadores/operaciones al cerrar.", ex);
        }

        FlushSettings();
        _disposed = true;

        try
        {
            _process.GameStarted -= OnGameStarted;
            _process.GameExited -= OnGameExited;
            _process.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Error al liberar el seguimiento del juego.", ex);
        }
    }
}
