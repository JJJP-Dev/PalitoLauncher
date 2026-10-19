using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Settings;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Helpers;

namespace PalitoLauncher.ViewModels;

/// <summary>Opción del límite de velocidad (ComboBox).</summary>
public sealed record SpeedLimitOption(string Label, int KBps);

/// <summary>Opción de "Al abrir el juego" (ComboBox).</summary>
public sealed record AfterLaunchOption(string Label, AfterLaunchBehavior Value);

/// <summary>Opción de "Modo de pantalla" (ComboBox).</summary>
public sealed record LaunchProfileOption(string Label, LaunchProfile Value);

/// <summary>Qué ajuste cambió (para que el MainViewModel reaccione además de guardar).</summary>
public enum SettingChange
{
    General,
    AutoDownload,
    IncludePrereleases,
    CheckLauncherUpdates,
    PlaytestReminders,
    DeveloperMode,
}

/// <summary>
/// Página de Ajustes. Lee y escribe directamente los ajustes que posee el MainViewModel; cada cambio se guarda al
/// momento (los argumentos de lanzamiento, con un retardo de 500 ms o al perder el foco).
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly DispatcherTimer _argumentsDebounce;
    private readonly ObservableCollection<SpeedLimitOption> _speedLimitOptions;
    private string _launchArguments = "";
    private string _installPathText = "Coop2D aún no está instalado.";
    private string _installSizeText = "";
    private bool _hasInstallation;
    private bool _isDriveMissing;
    private int _sizeVersion;
    private string? _selfUpdateNote;
    private string _feedbackNickname = "";
    private int _pendingFeedbackCount;
    private bool _pendingFeedbackWaitsForTeam;
    private int _pendingBugReportCount;
    private bool _pendingBugReportsWaitForTeam;

    public SettingsViewModel(MainViewModel owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        _speedLimitOptions = new ObservableCollection<SpeedLimitOption>
        {
            new("Sin límite", 0),
            new("512 KB/s", 512),
            new("1 MB/s", 1024),
            new("2 MB/s", 2 * 1024),
            new("5 MB/s", 5 * 1024),
            new("10 MB/s", 10 * 1024),
            new("20 MB/s", 20 * 1024),
        };
        SpeedLimitOptions = new ReadOnlyObservableCollection<SpeedLimitOption>(_speedLimitOptions);

        AfterLaunchOptions = new[]
        {
            new AfterLaunchOption("Mantener abierto", AfterLaunchBehavior.KeepOpen),
            new AfterLaunchOption("Minimizar", AfterLaunchBehavior.Minimize),
            new AfterLaunchOption("Cerrar launcher", AfterLaunchBehavior.Close),
        };

        LaunchProfileOptions = LaunchProfiles.All.Select(p => new LaunchProfileOption(LaunchProfiles.GetDisplayName(p), p)).ToList();

        _argumentsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _argumentsDebounce.Tick += (_, _) => CommitLaunchArguments();

        LauncherVersionText = $"{LauncherConfig.LauncherName} v{AppInfo.VersionText}";
    }

    private LauncherSettings S => _owner.CurrentSettings;

    // ───────────── Instalación ─────────────

    public string InstallPathText { get => _installPathText; private set => SetProperty(ref _installPathText, value); }
    public string InstallSizeText { get => _installSizeText; private set => SetProperty(ref _installSizeText, value); }
    public bool HasInstallation { get => _hasInstallation; private set => SetProperty(ref _hasInstallation, value); }

    /// <summary>La unidad de la carpeta guardada no está: se muestran "Elegir otra carpeta…" y "Olvidar esta carpeta".</summary>
    public bool IsDriveMissing { get => _isDriveMissing; private set => SetProperty(ref _isDriveMissing, value); }

    public ICommand OpenInstallFolderCommand => _owner.OpenInstallFolderCommand;
    public ICommand MoveInstallCommand => _owner.MoveInstallCommand;
    public ICommand UninstallCommand => _owner.UninstallCommand;
    public ICommand ChooseAnotherFolderCommand => _owner.ChooseAnotherFolderCommand;
    public ICommand ForgetInstallFolderCommand => _owner.ForgetInstallFolderCommand;

    // ───────────── Actualizaciones ─────────────

    public bool AutoDownloadUpdates
    {
        get => S.AutoDownloadUpdates;
        set
        {
            if (S.AutoDownloadUpdates == value) return;
            S.AutoDownloadUpdates = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.AutoDownload);
        }
    }

    public bool IncludePrereleases
    {
        get => S.IncludePrereleases;
        set
        {
            if (S.IncludePrereleases == value) return;
            S.IncludePrereleases = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.IncludePrereleases);
        }
    }

    public bool CheckLauncherUpdates
    {
        get => S.CheckLauncherUpdates;
        set
        {
            if (S.CheckLauncherUpdates == value) return;
            S.CheckLauncherUpdates = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.CheckLauncherUpdates);
        }
    }

    public ICommand CheckNowCommand => _owner.CheckNowCommand;

    // ───────────── Descargas ─────────────

    public ReadOnlyObservableCollection<SpeedLimitOption> SpeedLimitOptions { get; }

    public SpeedLimitOption? SelectedSpeedLimit
    {
        get => _speedLimitOptions.FirstOrDefault(o => o.KBps == S.MaxDownloadSpeedKBps);
        set
        {
            if (value == null || S.MaxDownloadSpeedKBps == value.KBps) return;
            S.MaxDownloadSpeedKBps = value.KBps;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.General);
        }
    }

    public bool KeepDownloadedArchives
    {
        get => S.KeepDownloadedArchives;
        set
        {
            if (S.KeepDownloadedArchives == value) return;
            S.KeepDownloadedArchives = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.General);
        }
    }

    public ICommand ClearDownloadsCommand => _owner.ClearDownloadsCommand;

    // ───────────── Juego ─────────────

    /// <summary>«Modo de pantalla»: perfiles de lanzamiento (LaunchProfiles), en el orden en que se muestran.</summary>
    public IReadOnlyList<LaunchProfileOption> LaunchProfileOptions { get; }

    /// <summary>Al abrir el juego se combina con los argumentos de lanzamiento (los del usuario mandan).</summary>
    public LaunchProfileOption? SelectedLaunchProfile
    {
        get => LaunchProfileOptions.FirstOrDefault(o => o.Value == S.LaunchProfile) ?? LaunchProfileOptions.FirstOrDefault();
        set
        {
            if (value == null || S.LaunchProfile == value.Value) return;
            S.LaunchProfile = value.Value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.General);
        }
    }

    /// <summary>Se guarda con un retardo de 500 ms o al perder el foco (<see cref="CommitLaunchArguments"/>).</summary>
    public string LaunchArguments
    {
        get => _launchArguments;
        set
        {
            if (!SetProperty(ref _launchArguments, value ?? "")) return;
            _argumentsDebounce.Stop();
            _argumentsDebounce.Start();
        }
    }

    public IReadOnlyList<AfterLaunchOption> AfterLaunchOptions { get; }

    public AfterLaunchOption? SelectedAfterLaunch
    {
        get => AfterLaunchOptions.FirstOrDefault(o => o.Value == S.AfterLaunch);
        set
        {
            if (value == null || S.AfterLaunch == value.Value) return;
            S.AfterLaunch = value.Value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.General);
        }
    }

    // ───────────── Sesiones de prueba (tarjeta Juego) ─────────────

    /// <summary>Notificación de Windows unos minutos antes de una sesión de prueba (si el launcher está abierto).</summary>
    public bool PlaytestRemindersEnabled
    {
        get => S.PlaytestRemindersEnabled;
        set
        {
            if (S.PlaytestRemindersEnabled == value) return;
            S.PlaytestRemindersEnabled = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.PlaytestReminders);
        }
    }

    // ───────────── Opiniones (tarjeta Juego) ─────────────

    /// <summary>Invitar a responder la encuesta al cerrar el juego tras una partida suficientemente larga.</summary>
    public bool AskFeedbackAfterSession
    {
        get => S.AskFeedbackAfterSession;
        set
        {
            if (S.AskFeedbackAfterSession == value) return;
            S.AskFeedbackAfterSession = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.General);
        }
    }

    /// <summary>Apodo para las opiniones. Se guarda al perder el foco (<see cref="CommitFeedbackNickname"/>).</summary>
    public string FeedbackNickname
    {
        get => _feedbackNickname;
        set
        {
            var text = value ?? "";
            if (text.Length > SurveyDialogViewModel.NicknameMaxLength) text = text[..SurveyDialogViewModel.NicknameMaxLength];
            SetProperty(ref _feedbackNickname, text);
        }
    }

    public int NicknameMaxLength => SurveyDialogViewModel.NicknameMaxLength;

    /// <summary>
    /// "1 opinión pendiente de enviar" / "3 opiniones pendientes de enviar"; sin webhook en la encuesta, "3 opiniones
    /// pendientes: se enviarán cuando el equipo active los envíos" (no es un problema de conexión).
    /// </summary>
    public string PendingFeedbackText => (_pendingFeedbackCount == 1, _pendingFeedbackWaitsForTeam) switch
    {
        (true, false) => "1 opinión pendiente de enviar",
        (false, false) => $"{_pendingFeedbackCount} opiniones pendientes de enviar",
        (true, true) => "1 opinión pendiente: se enviará cuando el equipo active los envíos",
        (false, true) => $"{_pendingFeedbackCount} opiniones pendientes: se enviarán cuando el equipo active los envíos",
    };

    public bool HasPendingFeedback => _pendingFeedbackCount > 0;

    /// <summary>Las pendientes esperan a que el equipo publique el webhook: se muestran sin color de aviso.</summary>
    public bool PendingFeedbackWaitsForTeam => _pendingFeedbackWaitsForTeam;

    public ICommand SendFeedbackCommand => _owner.SendFeedbackCommand;
    public ICommand RetryFeedbackSendCommand => _owner.RetryFeedbackSendCommand;

    // ───────────── Reportes de problemas (tarjeta Juego) ─────────────

    /// <summary>
    /// "1 reporte pendiente de enviar" / "3 reportes pendientes de enviar"; sin webhook, "…: se enviarán cuando el equipo
    /// active los envíos" (no es un problema de conexión).
    /// </summary>
    public string PendingBugReportsText => (_pendingBugReportCount == 1, _pendingBugReportsWaitForTeam) switch
    {
        (true, false) => "1 reporte de problema pendiente de enviar",
        (false, false) => $"{_pendingBugReportCount} reportes de problemas pendientes de enviar",
        (true, true) => "1 reporte de problema pendiente: se enviará cuando el equipo active los envíos",
        (false, true) => $"{_pendingBugReportCount} reportes de problemas pendientes: se enviarán cuando el equipo active los envíos",
    };

    public bool HasPendingBugReports => _pendingBugReportCount > 0;

    /// <summary>Los pendientes esperan a que el equipo publique un webhook: se muestran sin color de aviso.</summary>
    public bool PendingBugReportsWaitForTeam => _pendingBugReportsWaitForTeam;

    public ICommand ReportProblemCommand => _owner.ReportProblemCommand;
    public ICommand RetryBugReportSendCommand => _owner.RetryBugReportSendCommand;

    // ───────────── Herramientas de desarrollo ─────────────

    /// <summary>Muestra «Abrir otra instancia» (para probar host y cliente en el mismo PC).</summary>
    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set
        {
            if (S.DeveloperMode == value) return;
            S.DeveloperMode = value;
            OnPropertyChanged();
            _owner.OnSettingsChanged(SettingChange.DeveloperMode);
        }
    }

    public ICommand LaunchExtraInstanceCommand => _owner.LaunchExtraInstanceCommand;
    public ICommand ShowHostingCommand => _owner.ShowHostingCommand;

    // ───────────── Launcher ─────────────

    public string LauncherVersionText { get; }

    /// <summary>Motivo por el que no se puede auto-actualizar (p.ej. "Modo desarrollo"), o null.</summary>
    public string? SelfUpdateNote { get => _selfUpdateNote; private set => SetProperty(ref _selfUpdateNote, value); }

    public ICommand CreateDesktopShortcutCommand => _owner.CreateDesktopShortcutCommand;
    public ICommand OpenLogsFolderCommand => _owner.OpenLogsFolderCommand;
    public ICommand CreateReportCommand => _owner.CreateReportCommand;
    public ICommand OpenLauncherGitHubCommand => _owner.OpenLauncherGitHubCommand;

    // ───────────── API para el MainViewModel ─────────────

    /// <summary>Refresca todo tras cargar los ajustes del disco.</summary>
    internal void Reload()
    {
        _argumentsDebounce.Stop();
        _launchArguments = S.LaunchArguments ?? "";
        _feedbackNickname = S.FeedbackNickname ?? "";

        // Un valor guardado que no está en la lista (editado a mano) se añade para no perderlo.
        var kbps = Math.Max(0, S.MaxDownloadSpeedKBps);
        if (_speedLimitOptions.All(o => o.KBps != kbps))
            _speedLimitOptions.Add(new SpeedLimitOption($"{kbps} KB/s", kbps));

        OnPropertyChanged(string.Empty);
        _ = RefreshSelfUpdateNoteAsync();
    }

    /// <summary>Comprueba en segundo plano si el launcher puede auto-actualizarse (prueba de escritura en su carpeta).</summary>
    private async Task RefreshSelfUpdateNoteAsync()
    {
        try
        {
            var (can, reason) = await Task.Run(() =>
            {
                bool ok = LauncherSelfUpdater.CanSelfUpdate(out var why);
                return (ok, why);
            });
            SelfUpdateNote = can
                ? null
                : (string.IsNullOrWhiteSpace(reason) ? "La actualización automática del launcher no está disponible." : reason);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar si el launcher puede auto-actualizarse.", ex);
        }
    }

    /// <summary>Guarda los argumentos de lanzamiento pendientes (al perder el foco, por el retardo o al cerrar).</summary>
    public void CommitLaunchArguments()
    {
        _argumentsDebounce.Stop();
        var value = (_launchArguments ?? "").Trim();
        if (string.Equals(S.LaunchArguments ?? "", value, StringComparison.Ordinal)) return;
        S.LaunchArguments = value;
        _owner.OnSettingsChanged(SettingChange.General);
    }

    /// <summary>Guarda el apodo de las opiniones si cambió (al perder el foco o al cerrar). Vacío = anónimo (null).</summary>
    public void CommitFeedbackNickname()
    {
        var value = (_feedbackNickname ?? "").Trim();
        if (string.Equals(S.FeedbackNickname ?? "", value, StringComparison.Ordinal)) return;
        S.FeedbackNickname = value.Length > 0 ? value : null;
        _owner.OnSettingsChanged(SettingChange.General);
    }

    /// <summary>Vuelve a leer los ajustes de opiniones (los cambió la encuesta o la invitación).</summary>
    internal void ReloadFeedbackSettings()
    {
        _feedbackNickname = S.FeedbackNickname ?? "";
        OnPropertyChanged(nameof(FeedbackNickname));
        OnPropertyChanged(nameof(AskFeedbackAfterSession));
    }

    /// <summary>
    /// Número de opiniones en la bandeja de salida. <paramref name="waitsForTeam"/>: la encuesta no tiene webhook, así que
    /// esperan a que el equipo active los envíos.
    /// </summary>
    internal void SetPendingFeedback(int count, bool waitsForTeam = false)
    {
        count = Math.Max(0, count);
        if (_pendingFeedbackCount == count && _pendingFeedbackWaitsForTeam == waitsForTeam) return;
        _pendingFeedbackCount = count;
        _pendingFeedbackWaitsForTeam = waitsForTeam;
        OnPropertyChanged(nameof(PendingFeedbackText));
        OnPropertyChanged(nameof(HasPendingFeedback));
        OnPropertyChanged(nameof(PendingFeedbackWaitsForTeam));
    }

    /// <summary>
    /// Número de reportes de problemas en la bandeja de salida. <paramref name="waitsForTeam"/>: no hay webhook (ni en
    /// launcher.json ni en la encuesta), así que esperan a que el equipo active los envíos.
    /// </summary>
    internal void SetPendingBugReports(int count, bool waitsForTeam = false)
    {
        count = Math.Max(0, count);
        if (_pendingBugReportCount == count && _pendingBugReportsWaitForTeam == waitsForTeam) return;
        _pendingBugReportCount = count;
        _pendingBugReportsWaitForTeam = waitsForTeam;
        OnPropertyChanged(nameof(PendingBugReportsText));
        OnPropertyChanged(nameof(HasPendingBugReports));
        OnPropertyChanged(nameof(PendingBugReportsWaitForTeam));
    }

    /// <summary>Actualiza la tarjeta "Instalación" (ruta y tamaño en disco, calculado en segundo plano).</summary>
    internal void RefreshInstallInfo(string? installRoot, InstallStatus? status)
    {
        int version = ++_sizeVersion;
        HasInstallation = installRoot != null && status?.State is InstallState.Installed or InstallState.Broken;
        IsDriveMissing = installRoot != null && status?.State == InstallState.DriveMissing;

        if (installRoot == null || status == null || status.State == InstallState.NotInstalled)
        {
            InstallPathText = $"{LauncherConfig.GameDisplayName} aún no está instalado.";
            InstallSizeText = "";
            return;
        }

        InstallPathText = installRoot;
        if (status.State == InstallState.DriveMissing)
        {
            InstallSizeText = "La unidad no está disponible. Conéctala o elige otra carpeta para instalar el juego.";
            return;
        }

        InstallSizeText = "Calculando tamaño…";
        _ = ComputeSizeAsync(installRoot, version);
    }

    private async Task ComputeSizeAsync(string installRoot, int version)
    {
        try
        {
            var gameDir = GameInstaller.GetGameDir(installRoot);
            long size = await Task.Run(() => Directory.Exists(gameDir) ? FileSystemUtil.GetDirectorySize(gameDir) : 0L);
            if (version != _sizeVersion) return;
            InstallSizeText = size > 0 ? $"Tamaño en disco: {UiFormat.Bytes(size)}" : "";
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo calcular el tamaño de la instalación.", ex);
            if (version == _sizeVersion) InstallSizeText = "";
        }
    }
}
