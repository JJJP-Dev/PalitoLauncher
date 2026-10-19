using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace PalitoLauncher.Core.Net;

/// <summary>
/// HttpClient compartido para toda la app (una sola instancia, como recomienda .NET).
/// SocketsHttpHandler con PooledConnectionLifetime = 5 min, ConnectTimeout = 15 s, AllowAutoRedirect = true,
/// AutomaticDecompression = None (importante: las descargas con Range deben recibir los bytes tal cual).
/// HttpClient.Timeout = Infinite: cada llamada controla su propio timeout con CancellationToken.
/// Cabecera User-Agent obligatoria para la API de GitHub: "PalitoLauncher/&lt;versión&gt; (+https://github.com/JJJP-Dev/PalitoLauncher)".
/// </summary>
public static class HttpFactory
{
    private const string ProductName = "PalitoLauncher";
    private const string ProductComment = "(+https://github.com/JJJP-Dev/PalitoLauncher)";

    private static readonly Lazy<string> _version = new(GetProductVersion, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<HttpClient> _shared = new(() => Create(CreateDefaultHandler()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Instancia compartida (lazy, thread-safe).</summary>
    public static HttpClient Shared => _shared.Value;

    /// <summary>User-Agent usado en todas las peticiones.</summary>
    public static string UserAgent => $"{ProductName}/{_version.Value} {ProductComment}";

    /// <summary>Crea un HttpClient nuevo con la misma configuración sobre el handler dado (para tests).</summary>
    public static HttpClient Create(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        ApplyUserAgent(client.DefaultRequestHeaders);
        return client;
    }

    private static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.None,
    };

    private static void ApplyUserAgent(HttpRequestHeaders headers)
    {
        try
        {
            headers.UserAgent.Add(new ProductInfoHeaderValue(ProductName, _version.Value));
            headers.UserAgent.Add(new ProductInfoHeaderValue(ProductComment));
        }
        catch (FormatException)
        {
            // No debería pasar (la versión se sanea), pero el User-Agent es obligatorio para GitHub.
            headers.UserAgent.Clear();
            headers.TryAddWithoutValidation("User-Agent", $"{ProductName}/1.0 {ProductComment}");
        }
    }

    /// <summary>
    /// Versión del launcher: InformationalVersion del ensamblado de entrada si es nuestro (la app WPF), si no la del Core
    /// (ambos comparten la misma versión). Se corta en '+' y se dejan solo caracteres válidos en un token HTTP.
    /// </summary>
    private static string GetProductVersion()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly();
            var assembly = entry?.GetName().Name?.StartsWith(ProductName, StringComparison.OrdinalIgnoreCase) == true
                ? entry
                : typeof(HttpFactory).Assembly;

            var raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString();
            return SanitizeToken(raw);
        }
        catch (Exception ex)
        {
            Logging.Log.Warn("No se pudo leer la versión del launcher para el User-Agent.", ex);
            return "1.0";
        }
    }

    private static string SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "1.0";

        var plus = raw.IndexOf('+');
        if (plus >= 0) raw = raw[..plus];

        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw.Trim())
        {
            if (c is (>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '.' or '-' or '_')
                sb.Append(c);
        }
        if (sb.Length > 64) sb.Length = 64;
        return sb.Length == 0 ? "1.0" : sb.ToString();
    }
}
