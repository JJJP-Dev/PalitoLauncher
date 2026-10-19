using System.Windows.Input;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

// Herramientas: perfiles de lanzamiento (modo de pantalla), «Hostear partida: mi IP» y modo desarrollador (abrir otra
// instancia del juego para probar host y cliente en el mismo PC).
public sealed partial class MainViewModel
{
    private bool _hostingOpen;
    private Func<string, string, string?, int>? _extraInstanceLauncher;

    // ───────────── Comandos y estado ─────────────

    /// <summary>Menú ⋯ › «Hostear partida: mi IP».</summary>
    public ICommand ShowHostingCommand { get; }

    /// <summary>Modo desarrollador: «Abrir otra instancia» (junto a JUGAR, en ⋯ y en Ajustes).</summary>
    public ICommand LaunchExtraInstanceCommand { get; }

    /// <summary>Ajustes › Herramientas de desarrollo › «Modo desarrollador» (muestra sus botones).</summary>
    public bool IsDeveloperMode => _settings.DeveloperMode;

    // ───────────── Inyectables (los tests los sustituyen: sin red, sin portapapeles y sin abrir procesos) ─────────────

    /// <summary>IP locales (NetworkInfo); se llama en un hilo de fondo.</summary>
    internal Func<IReadOnlyList<LocalAddress>> GetLocalAddresses { get; set; } = NetworkInfo.GetLocalIPv4Addresses;

    /// <summary>IP pública (servicio externo): solo cuando el usuario pulsa «Mostrar mi IP pública».</summary>
    internal Func<CancellationToken, Task<string?>> GetPublicIp { get; set; } =
        static ct => NetworkInfo.GetPublicIpAsync(HttpFactory.Shared, ct);

    /// <summary>Copia al portapapeles (con reintentos); false si otra aplicación lo tiene ocupado.</summary>
    internal Func<string, Task<bool>> CopyText { get; set; } = ClipboardService.TrySetTextAsync;

    /// <summary>Arranca una instancia extra del juego (carpeta, exe, argumentos) y devuelve su PID.</summary>
    internal Func<string, string, string?, int> ExtraInstanceLauncher
    {
        get => _extraInstanceLauncher ??= _process.LaunchAdditionalInstance;
        set => _extraInstanceLauncher = value;
    }

    // ───────────── Perfiles de lanzamiento ─────────────

    /// <summary>
    /// Argumentos con los que se abre el juego: los del modo de pantalla elegido (Ajustes › Juego) seguidos de los del
    /// usuario, que mandan (LaunchProfiles.Combine). Nunca lanza.
    /// </summary>
    internal string BuildLaunchArguments()
    {
        try
        {
            return LaunchProfiles.Combine(_settings.LaunchProfile, _settings.LaunchArguments);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudieron combinar el modo de pantalla y los argumentos; se usan solo los argumentos.", ex);
            return _settings.LaunchArguments ?? "";
        }
    }

    private static string LaunchProfileNameSafe(LaunchProfile profile)
    {
        try
        {
            return LaunchProfiles.GetDisplayName(profile);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer el nombre del modo de pantalla.", ex);
            return profile.ToString();
        }
    }

    // ───────────── Hostear partida ─────────────

    /// <summary>Diálogo con las IP para que los amigos se unan (las locales se leen en segundo plano). Uno a la vez.</summary>
    private async Task ShowHostingAsync()
    {
        if (_hostingOpen || _disposed) return;
        _hostingOpen = true;
        try
        {
            var getLocal = GetLocalAddresses;
            IReadOnlyList<LocalAddress> addresses;
            try
            {
                addresses = await Task.Run(() => getLocal());
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudieron leer las IP locales para hostear.", ex);
                addresses = Array.Empty<LocalAddress>();
            }
            if (_disposed) return;

            var hosting = _manifest.Hosting;
            var dialog = new HostingDialogViewModel(addresses, hosting?.Port, hosting?.Notes, GetPublicIp, CopyText);
            Log.Info($"Hostear partida: {addresses.Count} IP local(es){(hosting?.Port is { } port ? $", puerto {port}" : "")}.");
            await Dialogs.ShowDialogAsync(dialog, dialog.Completion);
        }
        finally
        {
            _hostingOpen = false;
        }
    }

    // ───────────── Modo desarrollador ─────────────

    /// <summary>
    /// Abre otra instancia del juego instalado con el mismo modo de pantalla y argumentos (sin seguirla: no cambia JUGAR).
    /// Sin juego instalado o con una operación en curso, solo avisa.
    /// </summary>
    private async Task LaunchExtraInstanceAsync()
    {
        if (!_settings.DeveloperMode) return;
        var status = _installStatus;
        if (status?.State != InstallState.Installed || string.IsNullOrEmpty(status.GameDir))
        {
            ShowToast($"Instala {Game} primero para poder abrir otra instancia.");
            return;
        }
        if (IsBusy)
        {
            ShowToast(BusyMessage);
            return;
        }

        var gameDir = status.GameDir;
        var exe = GameExecutableName;
        var arguments = BuildLaunchArguments();
        var launcher = ExtraInstanceLauncher;
        int pid = await Task.Run(() => launcher(gameDir, exe, arguments));
        Log.Info($"Modo desarrollador: instancia extra abierta (pid {pid}).");
        ShowToast("Se abrió otra instancia del juego. Si no aparece una ventana nueva, el juego tiene «Force Single Instance» activado.");
    }
}
