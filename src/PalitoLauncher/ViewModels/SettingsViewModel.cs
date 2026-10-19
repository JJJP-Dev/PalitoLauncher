using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Install;
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

/// <summary>Qué ajuste cambió (para que el MainViewModel reaccione además de guardar).</summary>
public enum SettingChange
{
    General,
    AutoDownload,
    IncludePrereleases,
    CheckLauncherUpdates,
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
