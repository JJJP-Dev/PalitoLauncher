using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Helpers;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Resultado del diálogo de instalación.</summary>
/// <param name="InstallRoot">Carpeta ya resuelta con GameInstaller.ResolveInstallRoot.</param>
/// <param name="CreateDesktopShortcut">Crear el acceso directo del escritorio.</param>
/// <param name="AdoptExisting">
/// La carpeta ya contiene una instalación válida de la MISMA build que se pidió (mismo SHA-256 o asset): se adopta sin
/// descargar. Si contiene otra versión es false y se instala encima la pedida.
/// </param>
public sealed record InstallDialogResult(string InstallRoot, bool CreateDesktopShortcut, bool AdoptExisting);

/// <summary>
/// Diálogo "Instalar Coop2D": elegir carpeta, ver dónde se instalará, espacio necesario/libre y avisos de la ubicación.
/// La validación (E/S) se hace en segundo plano con un pequeño retardo mientras se escribe. Mientras se confirma
/// (prueba de escritura) la ruta no se puede editar; si aun así cambió, no se confirma y el usuario vuelve a pulsar.
/// </summary>
public sealed class InstallDialogViewModel : ObservableObject, IOverlayDialog
{
    private readonly GameRelease _release;
    private readonly Func<string, InstallStatus> _getStatus;
    private readonly TaskCompletionSource<InstallDialogResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _debounce;
    private readonly AsyncRelayCommand _installCommand;
    private int _validationVersion;
    private Task _validationTask = Task.CompletedTask;

    private string _pickedFolder;
    private string? _resolvedRoot;
    private bool _existingInstall;
    private string _resolvedPathText = "";
    private string _spaceText = "";
    private bool _isSpaceInsufficient;
    private string? _locationMessage;
    private bool _isLocationBlocked;
    private bool _isLocationWarning;
    private bool _isValidating;
    private bool _isConfirming;
    private bool _createDesktopShortcut = true;

    public InstallDialogViewModel(GameRelease release, string proposedFolder, Func<string, InstallStatus> getStatus,
        bool showShortcutOption)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
        _pickedFolder = proposedFolder ?? "";
        ShowShortcutOption = showShortcutOption;

        Title = $"Instalar {LauncherConfig.GameDisplayName}";
        VersionText = release.Asset.Size > 0
            ? $"Versión {release.VersionDisplay} · descarga de {UiFormat.Bytes(release.Asset.Size)}"
            : $"Versión {release.VersionDisplay}";

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _validationTask = ValidateAsync();
        };

        ChangeFolderCommand = new RelayCommand(ChangeFolder, () => !IsConfirming);
        _installCommand = new AsyncRelayCommand(ConfirmAsync, () => CanInstall);
        InstallCommand = _installCommand;
        CancelCommand = new RelayCommand(Cancel);
    }

    public string Title { get; }
    public string VersionText { get; }
    public bool ShowShortcutOption { get; }
    public bool IsDanger => false;

    /// <summary>Carpeta elegida o escrita por el usuario (sin resolver).</summary>
    public string PickedFolder
    {
        get => _pickedFolder;
        set
        {
            if (!SetProperty(ref _pickedFolder, value ?? "")) return;
            _validationVersion++; // invalida cualquier validación en curso del texto anterior
            IsValidating = true;  // deshabilita Instalar hasta validar el texto nuevo
            _debounce.Stop();
            _debounce.Start();
        }
    }

    /// <summary>"Se instalará en: D:\Juegos\Coop2D".</summary>
    public string ResolvedPathText { get => _resolvedPathText; private set => SetProperty(ref _resolvedPathText, value); }

    /// <summary>"Espacio necesario: ~247.7 MB · Libre: 120.30 GB".</summary>
    public string SpaceText { get => _spaceText; private set => SetProperty(ref _spaceText, value); }

    public bool IsSpaceInsufficient { get => _isSpaceInsufficient; private set => SetProperty(ref _isSpaceInsufficient, value); }

    public string? LocationMessage
    {
        get => _locationMessage;
        private set
        {
            if (SetProperty(ref _locationMessage, value)) OnPropertyChanged(nameof(HasLocationMessage));
        }
    }

    public bool HasLocationMessage => !string.IsNullOrWhiteSpace(_locationMessage);
    public bool IsLocationBlocked { get => _isLocationBlocked; private set => SetProperty(ref _isLocationBlocked, value); }
    public bool IsLocationWarning { get => _isLocationWarning; private set => SetProperty(ref _isLocationWarning, value); }

    public bool IsValidating
    {
        get => _isValidating;
        private set
        {
            if (!SetProperty(ref _isValidating, value)) return;
            OnPropertyChanged(nameof(IsChecking));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Comprobando la escritura antes de cerrar: la ruta y la casilla quedan bloqueadas.</summary>
    public bool IsConfirming
    {
        get => _isConfirming;
        private set
        {
            if (!SetProperty(ref _isConfirming, value)) return;
            OnPropertyChanged(nameof(IsChecking));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Muestra "Comprobando…" (validando la ruta o confirmando).</summary>
    public bool IsChecking => _isValidating || _isConfirming;

    public bool CreateDesktopShortcut { get => _createDesktopShortcut; set => SetProperty(ref _createDesktopShortcut, value); }

    public bool CanInstall => !IsValidating && !IsConfirming && _resolvedRoot != null && !IsLocationBlocked && !IsSpaceInsufficient;

    public ICommand ChangeFolderCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand CancelCommand { get; }

    public Task<InstallDialogResult?> Completion => _completion.Task;

    /// <summary>Primera validación (llamar al abrir el diálogo).</summary>
    public void StartValidation()
    {
        IsValidating = true;
        _validationTask = ValidateAsync();
    }

    public void Cancel()
    {
        _debounce.Stop();
        _completion.TrySetResult(null);
    }

    public void Accept()
    {
        if (_installCommand.CanExecute(null)) _installCommand.Execute(null);
    }

    private void ChangeFolder()
    {
        // Proponer la carpeta padre si la actual ya acaba en "\Coop2D" (el usuario elige "dónde", no la carpeta final).
        var initial = _resolvedRoot ?? PickedFolder;
        try
        {
            if (!string.IsNullOrWhiteSpace(initial)
                && string.Equals(Path.GetFileName(initial.TrimEnd('\\', '/')), LauncherConfig.InstallFolderName, StringComparison.OrdinalIgnoreCase))
            {
                initial = Path.GetDirectoryName(initial.TrimEnd('\\', '/')) ?? initial;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Ruta inicial no válida para el selector: {ex.Message}");
        }

        var picked = DialogService.PickFolder($"Elige dónde instalar {LauncherConfig.GameDisplayName}", initial);
        if (picked != null) PickedFolder = picked;
    }

    private async Task ValidateAsync()
    {
        int version = ++_validationVersion;
        IsValidating = true;
        var picked = PickedFolder;

        ValidationResult result;
        try
        {
            result = await Task.Run(() => Validate(picked));
        }
        catch (Exception ex)
        {
            Log.Error("Error al validar la carpeta de instalación.", ex);
            result = ValidationResult.Blocked("No se pudo comprobar la carpeta. Elige otra.");
        }

        if (version != _validationVersion) return; // llegó otra validación más nueva

        _resolvedRoot = result.ResolvedRoot;
        _existingInstall = result.ExistingInstall;
        ResolvedPathText = result.ResolvedRoot != null ? $"Se instalará en: {result.ResolvedRoot}" : "";
        SpaceText = result.SpaceText;
        IsSpaceInsufficient = result.SpaceInsufficient;
        IsLocationBlocked = result.IsBlocked;
        IsLocationWarning = result.IsWarning;
        LocationMessage = result.Message;
        IsValidating = false;
    }

    private ValidationResult Validate(string picked)
    {
        var folder = (picked ?? "").Trim().Trim('"');
        if (folder.Length == 0) return ValidationResult.Blocked("Elige una carpeta para instalar el juego.");
        if (!Path.IsPathFullyQualified(folder)) return ValidationResult.Blocked("Escribe una ruta completa, por ejemplo D:\\Juegos.");

        string resolved;
        try
        {
            resolved = GameInstaller.ResolveInstallRoot(folder);
        }
        catch (LauncherException ex)
        {
            return ValidationResult.Blocked(ex.UserMessage);
        }
        catch (Exception ex)
        {
            Log.Warn($"Carpeta de instalación no válida: {folder}", ex);
            return ValidationResult.Blocked("La ruta no es válida.");
        }

        var check = FileSystemUtil.CheckInstallLocation(resolved, LauncherInstallation.GetPreferredLauncherExePath());
        bool blocked = check.IsBlocked;
        bool warning = check.IsWarning && !blocked;
        string? message = check.Reason;

        // ¿Ya hay un juego instalado ahí? Solo se adopta sin descargar si es la MISMA build que se pidió.
        InstalledGameInfo? installed = null;
        if (!blocked)
        {
            try
            {
                var status = _getStatus(resolved);
                if (status.State == InstallState.Installed) installed = status.Info;
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo comprobar si ya hay una instalación en la carpeta elegida.", ex);
            }
        }
        bool existing = installed != null && IsSameBuild(installed, _release);

        string spaceText;
        bool insufficient = false;
        if (existing)
        {
            spaceText = "Ya está instalado ahí: no hace falta descargar nada.";
        }
        else
        {
            long required = GameInstaller.EstimateRequiredBytes(_release);
            long free = FileSystemUtil.GetAvailableFreeSpace(resolved);
            bool unknown = free == long.MaxValue;
            insufficient = !unknown && free < required;
            spaceText = $"Espacio necesario: ~{UiFormat.Bytes(required)} · Libre: {(unknown ? "desconocido" : UiFormat.Bytes(free))}";
        }

        // Solo se prueba la escritura si la carpeta ya existe (para no crear carpetas al curiosear).
        if (!blocked && Directory.Exists(resolved) && !FileSystemUtil.IsDirectoryWritable(resolved))
        {
            blocked = true;
            warning = false;
            message = "No se puede escribir en esa carpeta. Elige otra.";
        }

        if (!blocked && installed != null)
        {
            string note;
            if (existing)
            {
                note = $"Ya hay una instalación de {LauncherConfig.GameDisplayName} en esta carpeta: se usará sin volver a descargar.";
            }
            else
            {
                var installedText = !string.IsNullOrWhiteSpace(installed.VersionDisplay) ? installed.VersionDisplay : installed.Tag;
                note = string.Equals(installedText, _release.VersionDisplay, StringComparison.OrdinalIgnoreCase)
                    ? $"En esta carpeta hay otra build de {installedText}: se reemplazará por la elegida. Tus partidas guardadas se conservan."
                    : $"En esta carpeta ya está instalada la versión {installedText}: se reemplazará por {_release.VersionDisplay}. Tus partidas guardadas se conservan.";
            }
            message = string.IsNullOrWhiteSpace(message) ? note : $"{message}\n\n{note}";
        }

        return new ValidationResult(resolved, spaceText, insufficient, message, blocked, warning, existing);
    }

    /// <summary>
    /// ¿La instalación <paramref name="info"/> es exactamente la build de <paramref name="release"/>? Mismo SHA-256 si
    /// ambos lo tienen; si no, mismo asset de GitHub.
    /// </summary>
    internal static bool IsSameBuild(InstalledGameInfo? info, GameRelease release)
    {
        if (info == null || release?.Asset == null) return false;
        if (!string.IsNullOrWhiteSpace(info.Sha256) && !string.IsNullOrWhiteSpace(release.Asset.Sha256))
            return string.Equals(info.Sha256.Trim(), release.Asset.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
        return info.AssetId != 0 && info.AssetId == release.Asset.Id;
    }

    private async Task ConfirmAsync()
    {
        // Si el usuario pulsa Instalar justo tras escribir, esperar a la validación en curso.
        await _validationTask;
        if (!CanInstall) return;

        var resolved = _resolvedRoot;
        if (resolved == null) return;

        // Todo lo que se va a devolver se captura ANTES de esperar: si la ruta cambia mientras tanto, no se confirma.
        int version = _validationVersion;
        bool existing = _existingInstall;
        bool shortcut = ShowShortcutOption && CreateDesktopShortcut;

        IsConfirming = true;
        try
        {
            bool writable = await Task.Run(() => FileSystemUtil.IsDirectoryWritable(resolved));
            if (version != _validationVersion || IsValidating)
            {
                // La ruta cambió durante la comprobación: se valida la nueva y el usuario vuelve a pulsar Instalar.
                Log.Info("La carpeta cambió mientras se confirmaba la instalación; no se confirma.");
                return;
            }

            if (!writable)
            {
                IsLocationWarning = false;
                IsLocationBlocked = true;
                LocationMessage = "No se puede escribir en esa carpeta. Elige otra.";
                return;
            }

            _debounce.Stop();
            _completion.TrySetResult(new InstallDialogResult(resolved, shortcut, existing));
        }
        catch (Exception ex)
        {
            Log.Error("Error al confirmar la carpeta de instalación.", ex);
            IsLocationWarning = false;
            IsLocationBlocked = true;
            LocationMessage = "No se pudo comprobar la carpeta. Elige otra.";
        }
        finally
        {
            IsConfirming = false;
        }
    }

    private sealed record ValidationResult(string? ResolvedRoot, string SpaceText, bool SpaceInsufficient, string? Message,
        bool IsBlocked, bool IsWarning, bool ExistingInstall)
    {
        public static ValidationResult Blocked(string message) => new(null, "", false, message, true, false, false);
    }
}
