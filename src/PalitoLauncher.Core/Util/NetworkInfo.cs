using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Util;

/// <summary>Dirección IPv4 local de una interfaz de red.</summary>
public sealed record LocalAddress(string Address, string InterfaceName, bool IsPrivate);

/// <summary>Datos de red para hostear partidas (antes de Steam): IPs locales y pública.</summary>
public static partial class NetworkInfo
{
    /// <summary>Servicios que devuelven la IP pública en texto plano, en orden de preferencia.</summary>
    internal static readonly string[] PublicIpServices =
    {
        "https://api.ipify.org",
        "https://checkip.amazonaws.com",
    };

    internal static readonly TimeSpan PublicIpTimeout = TimeSpan.FromSeconds(5);

    // Una IP en texto ocupa como mucho ~45 caracteres; se lee un poco más por si viene con espacios o saltos de línea.
    private const int MaxPublicIpResponseChars = 256;

    // Nombres/descripciones de adaptadores virtuales típicos (máquinas virtuales, WSL, Docker): van al final de la lista.
    private static readonly string[] VirtualAdapterMarkers =
    {
        "Hyper-V", "VirtualBox", "VMware", "WSL", "vEthernet",
    };

    /// <summary>
    /// IPv4 de las interfaces activas (OperationalStatus.Up), sin loopback, sin túnel ni APIPA (169.254.x.x). Orden: privadas
    /// (10/8, 172.16/12, 192.168/16) primero, luego por si la interfaz tiene puerta de enlace, luego por nombre. Las de
    /// adaptadores virtuales típicos (Hyper-V, VirtualBox, VMware, WSL, vEthernet) van al final. Nunca lanza (lista vacía).
    /// </summary>
    public static IReadOnlyList<LocalAddress> GetLocalIPv4Addresses()
    {
        try
        {
            var candidates = new List<InterfaceAddress>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up
                        || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                        continue;

                    var properties = nic.GetIPProperties();
                    var hasGateway = properties.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));

                    foreach (var unicast in properties.UnicastAddresses)
                    {
                        candidates.Add(new InterfaceAddress(unicast.Address, nic.Name, nic.Description,
                            nic.NetworkInterfaceType, nic.OperationalStatus, hasGateway));
                    }
                }
                catch (Exception ex)
                {
                    // Una interfaz rara (driver de VPN, adaptador que desaparece) no debe impedir listar las demás.
                    Log.Debug($"No se pudo leer la interfaz de red '{SafeName(nic)}': {ex.Message}");
                }
            }

            return SelectAndOrder(candidates);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudieron leer las direcciones IP locales.", ex);
            return Array.Empty<LocalAddress>();
        }
    }

    /// <summary>
    /// IP pública: GET https://api.ipify.org (texto) con timeout de 5 s; si falla, https://checkip.amazonaws.com. Solo se
    /// acepta si parsea como IPv4/IPv6 válida. Null si no se pudo. Solo lanza OperationCanceledException si se canceló
    /// <paramref name="ct"/>. (Solo se llama cuando el usuario lo pide: es una petición a un servicio externo.)
    /// </summary>
    public static Task<string?> GetPublicIpAsync(HttpClient http, CancellationToken ct) =>
        GetPublicIpAsync(http, PublicIpTimeout, ct);

    /// <summary>Como <see cref="GetPublicIpAsync(HttpClient, CancellationToken)"/> con otro timeout por servicio (para tests).</summary>
    internal static async Task<string?> GetPublicIpAsync(HttpClient http, TimeSpan timeoutPerService, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ct.ThrowIfCancellationRequested();

        foreach (var url in PublicIpServices)
        {
            var host = new Uri(url).Host;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutPerService);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"{host} respondió {(int)response.StatusCode} al pedir la IP pública.");
                    continue;
                }

                var text = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                var ip = ParsePublicIp(text);
                if (ip != null)
                {
                    // La IP no se escribe en el log: el log puede adjuntarse a un reporte de problemas.
                    Log.Info($"IP pública obtenida de {host}.");
                    return ip;
                }

                Log.Warn($"{host} devolvió algo que no es una IP.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"{host} tardó demasiado en devolver la IP pública.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"No se pudo pedir la IP pública a {host}: {ex.Message}");
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- internos (separados para poder probarlos sin red)

    /// <summary>Dirección de una interfaz tal como la da Windows (antes de filtrar y ordenar).</summary>
    internal sealed record InterfaceAddress(
        IPAddress Address,
        string InterfaceName,
        string Description,
        NetworkInterfaceType Type,
        OperationalStatus Status,
        bool HasGateway);

    /// <summary>Filtra (solo IPv4 útiles de interfaces activas) y ordena como dice <see cref="GetLocalIPv4Addresses"/>. Sin repetidas.</summary>
    internal static IReadOnlyList<LocalAddress> SelectAndOrder(IEnumerable<InterfaceAddress> candidates)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return candidates
            .Where(c => c.Status == OperationalStatus.Up
                        && c.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                        && c.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(c.Address)
                        && !IsLinkLocal(c.Address)
                        && !c.Address.Equals(IPAddress.Any))
            .Select(c => (Candidate: c, IsPrivate: IsPrivateIPv4(c.Address), IsVirtual: IsVirtualAdapter(c.InterfaceName, c.Description)))
            .OrderBy(x => x.IsVirtual ? 1 : 0)
            .ThenBy(x => x.IsPrivate ? 0 : 1)
            .ThenBy(x => x.Candidate.HasGateway ? 0 : 1)
            .ThenBy(x => x.Candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new LocalAddress(x.Candidate.Address.ToString(), x.Candidate.InterfaceName, x.IsPrivate))
            .Where(a => seen.Add(a.Address))
            .ToList();
    }

    /// <summary>¿Es una IPv4 privada (10.0.0.0/8, 172.16.0.0/12 o 192.168.0.0/16)?</summary>
    internal static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>¿Es un adaptador virtual típico (Hyper-V, VirtualBox, VMware, WSL, vEthernet)? Mira el nombre y la descripción.</summary>
    internal static bool IsVirtualAdapter(string? name, string? description) =>
        VirtualAdapterMarkers.Any(marker =>
            (name?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false)
            || (description?.Contains(marker, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// Valida lo que devuelve el servicio de IP pública: una sola IPv4 en notación decimal con puntos (a.b.c.d) o una IPv6,
    /// sin nada más. Devuelve la IP normalizada o null. (IPAddress.TryParse solo aceptaría cosas como "1" → 0.0.0.1.)
    /// </summary>
    internal static string? ParsePublicIp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length > 45)
            return null;

        IPAddress? address;
        if (trimmed.Contains(':'))
        {
            // IPv6: sin zona ("%12"), que solo tiene sentido en la red local.
            if (trimmed.Contains('%') || !IPAddress.TryParse(trimmed, out address)
                || address.AddressFamily != AddressFamily.InterNetworkV6)
                return null;
        }
        else
        {
            if (!DottedIPv4Regex().IsMatch(trimmed) || !IPAddress.TryParse(trimmed, out address)
                || address.AddressFamily != AddressFamily.InterNetwork)
                return null;
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
            return null;

        return address.ToString();
    }

    [GeneratedRegex(@"^(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(\.(25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedIPv4Regex();

    private static bool IsLinkLocal(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxPublicIpResponseChars];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return new string(buffer, 0, total);
    }

    private static string SafeName(NetworkInterface nic)
    {
        try
        {
            return nic.Name;
        }
        catch (Exception)
        {
            return "?";
        }
    }
}
