using System.Globalization;
using System.Windows.Input;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Una IP local de la lista (con su botón «Copiar»).</summary>
public sealed class HostAddressViewModel
{
    internal HostAddressViewModel(LocalAddress address, Func<HostAddressViewModel, Task> copy)
    {
        Address = address.Address;
        InterfaceName = string.IsNullOrWhiteSpace(address.InterfaceName) ? "Conexión de red" : address.InterfaceName.Trim();
        IsPrivate = address.IsPrivate;
        CopyCommand = new AsyncRelayCommand(() => copy(this));
    }

    public string Address { get; }

    /// <summary>Nombre de la conexión en Windows ("Wi-Fi", "Ethernet"…).</summary>
    public string InterfaceName { get; }

    public bool IsPrivate { get; }

    public string CopyAutomationName => $"Copiar la IP {Address}";

    public ICommand CopyCommand { get; }
}

/// <summary>
/// Diálogo overlay «Hostear partida»: las IP para que los amigos se unan (la primera privada destacada como «IP en tu red
/// (LAN)»), la IP pública solo si se pide (consulta un servicio externo), «Copiar» con aviso si el portapapeles está
/// ocupado, y el puerto y las notas de launcher.json (hosting) si existen. Esc, Enter o «Cerrar» lo cierran.
/// </summary>
public sealed class HostingDialogViewModel : ObservableObject, IOverlayDialog
{
    internal const string PublicIpErrorText =
        "No se pudo obtener tu IP pública. Revisa tu conexión a internet y vuelve a intentarlo.";

    internal const string CopyFailedText =
        "No se pudo copiar: puede que otra aplicación esté usando el portapapeles. Vuelve a intentarlo.";

    private readonly Func<CancellationToken, Task<string?>> _getPublicIp;
    private readonly Func<string, Task<bool>> _copy;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private string? _publicIp;
    private bool _isFetchingPublicIp;
    private string? _publicIpError;
    private string? _copyStatusText;
    private bool _isCopyError;

    /// <param name="addresses">IP locales ya ordenadas (NetworkInfo.GetLocalIPv4Addresses).</param>
    /// <param name="port">Puerto de launcher.json (hosting.port), si lo hay.</param>
    /// <param name="notes">Indicaciones de launcher.json (hosting.notes), si las hay.</param>
    /// <param name="getPublicIp">Consulta la IP pública (solo al pulsar el botón).</param>
    /// <param name="copy">Copia al portapapeles; false si no se pudo.</param>
    public HostingDialogViewModel(IReadOnlyList<LocalAddress>? addresses, int? port, string? notes,
        Func<CancellationToken, Task<string?>> getPublicIp, Func<string, Task<bool>> copy)
    {
        _getPublicIp = getPublicIp ?? throw new ArgumentNullException(nameof(getPublicIp));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));

        var all = (addresses ?? Array.Empty<LocalAddress>())
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Address))
            .Select(a => new HostAddressViewModel(a, item => CopyAsync(item.Address)))
            .ToList();
        LanAddress = all.FirstOrDefault(a => a.IsPrivate);
        OtherAddresses = all.Where(a => !ReferenceEquals(a, LanAddress)).ToList();

        PortText = port is int p and >= 1 and <= 65535 ? $"Puerto del juego: {p.ToString(CultureInfo.InvariantCulture)}" : null;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        ShowPublicIpCommand = new AsyncRelayCommand(FetchPublicIpAsync, () => !_isFetchingPublicIp);
        CopyPublicIpCommand = new AsyncRelayCommand(() => CopyAsync(_publicIp ?? ""), () => _publicIp != null);
        CloseCommand = new RelayCommand(Close);
    }

    public string Title => "Hostear partida";
    public string Intro => "Para que tus amigos se unan a tu partida, crea la partida en el juego y diles tu IP.";
    public bool IsDanger => false;

    // ───────────── IP locales ─────────────

    /// <summary>La primera IP privada: la que sirve si estáis en la misma casa o red.</summary>
    public HostAddressViewModel? LanAddress { get; }

    public bool HasLanAddress => LanAddress != null;

    public string LanTitle => "IP en tu red (LAN)";
    public string LanHint => "Para amigos en tu misma casa o conectados a tu misma red / Wi-Fi.";

    /// <summary>El resto (otras conexiones, adaptadores virtuales…).</summary>
    public IReadOnlyList<HostAddressViewModel> OtherAddresses { get; }

    public bool HasOtherAddresses => OtherAddresses.Count > 0;

    public string OtherAddressesTitle => HasLanAddress ? "Otras direcciones de este PC" : "Direcciones de este PC";

    public bool HasNoAddresses => !HasLanAddress && !HasOtherAddresses;

    public string NoAddressesText =>
        "No se encontró ninguna conexión de red activa. Comprueba que estás conectado a internet o a tu red.";

    // ───────────── IP pública ─────────────

    public string PublicIpTitle => "IP pública (internet)";
    public string PublicIpHint => "Para amigos que están en otra casa.";

    public string PublicIpNote =>
        "Al pulsar el botón se consulta tu IP a un servicio externo (ipify.org). No la compartas en sitios públicos.";

    public string? PublicIp
    {
        get => _publicIp;
        private set
        {
            if (!SetProperty(ref _publicIp, value)) return;
            OnPropertyChanged(nameof(HasPublicIp));
            OnPropertyChanged(nameof(ShowPublicIpText));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasPublicIp => _publicIp != null;

    public bool IsFetchingPublicIp
    {
        get => _isFetchingPublicIp;
        private set
        {
            if (!SetProperty(ref _isFetchingPublicIp, value)) return;
            OnPropertyChanged(nameof(ShowPublicIpText));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public string ShowPublicIpText => _isFetchingPublicIp ? "Consultando…" : _publicIp != null ? "Volver a consultar" : "Mostrar mi IP pública";

    public string? PublicIpError
    {
        get => _publicIpError;
        private set
        {
            if (SetProperty(ref _publicIpError, value)) OnPropertyChanged(nameof(HasPublicIpError));
        }
    }

    public bool HasPublicIpError => _publicIpError != null;

    // ───────────── launcher.json y ayuda ─────────────

    /// <summary>"Puerto del juego: 7770" (hosting.port de launcher.json) o null.</summary>
    public string? PortText { get; }

    public bool HasPort => PortText != null;

    /// <summary>hosting.notes de launcher.json (p.ej. «Abre el puerto UDP 7770 en tu router o usa Radmin VPN»).</summary>
    public string? Notes { get; }

    public bool HasNotes => Notes != null;

    public string HelpText =>
        "Si tus amigos están en otra casa, tienes que abrir el puerto del juego en tu router (busca «abrir puertos» y el modelo " +
        "de tu router) o usar una VPN para jugar como Radmin VPN o ZeroTier (entonces se usa la IP que te da la VPN, que " +
        "aparecerá en la lista). Si estáis en la misma red, basta con la IP en tu red (LAN).";

    // ───────────── Copiar ─────────────

    /// <summary>"Copiado: 192.168.1.20" o el aviso de que el portapapeles está ocupado.</summary>
    public string? CopyStatusText
    {
        get => _copyStatusText;
        private set
        {
            if (SetProperty(ref _copyStatusText, value)) OnPropertyChanged(nameof(HasCopyStatus));
        }
    }

    public bool HasCopyStatus => _copyStatusText != null;

    public bool IsCopyError { get => _isCopyError; private set => SetProperty(ref _isCopyError, value); }

    public ICommand ShowPublicIpCommand { get; }
    public ICommand CopyPublicIpCommand { get; }
    public ICommand CloseCommand { get; }

    public Task<bool> Completion => _completion.Task;

    public void Cancel() => Close();

    public void Accept() => Close();

    private void Close()
    {
        if (_completion.Task.IsCompleted) return;
        try
        {
            _cts.Cancel(); // una consulta de la IP pública en curso ya no hace falta
        }
        catch (ObjectDisposedException)
        {
            // ya liberado
        }
        _completion.TrySetResult(true);
    }

    private async Task FetchPublicIpAsync()
    {
        if (_isFetchingPublicIp || _completion.Task.IsCompleted) return;
        PublicIpError = null;
        IsFetchingPublicIp = true;
        try
        {
            var ip = await _getPublicIp(_cts.Token);
            if (_completion.Task.IsCompleted) return;
            if (string.IsNullOrWhiteSpace(ip))
            {
                PublicIpError = PublicIpErrorText;
                return;
            }
            PublicIp = ip.Trim();
        }
        catch (OperationCanceledException)
        {
            // Se cerró el diálogo.
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo obtener la IP pública.", ex);
            PublicIpError = PublicIpErrorText;
        }
        finally
        {
            IsFetchingPublicIp = false;
        }
    }

    private async Task CopyAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        bool ok;
        try
        {
            ok = await _copy(text);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo copiar la IP al portapapeles.", ex);
            ok = false;
        }
        IsCopyError = !ok;
        CopyStatusText = ok ? $"Copiado: {text}" : CopyFailedText;
    }
}
