using System.Text;

namespace PalitoLauncher.Core.Launch;

/// <summary>Perfiles de lanzamiento (argumentos de línea de comandos del reproductor de Unity).</summary>
public enum LaunchProfile
{
    /// <summary>Lo que diga el juego (sin argumentos).</summary>
    Default,

    /// <summary>Ventana 1280×720.</summary>
    Windowed720,

    /// <summary>Ventana 1920×1080.</summary>
    Windowed1080,

    /// <summary>Pantalla completa en ventana sin bordes.</summary>
    Borderless,

    /// <summary>Pantalla completa exclusiva.</summary>
    Fullscreen,
}

public static class LaunchProfiles
{
    // Cada perfil como pares (opción, valor). Comprobado en la documentación de Unity 6 («Unity Standalone Player command
    // line arguments»): -screen-fullscreen 0|1, -screen-width/-screen-height (enteros) y -window-mode exclusive|borderless
    // (este último solo en Windows, que es nuestro caso).
    private static readonly (string Option, string Value)[] NoArguments = Array.Empty<(string, string)>();

    private static readonly (string Option, string Value)[] Windowed720Arguments =
        { ("-screen-fullscreen", "0"), ("-screen-width", "1280"), ("-screen-height", "720") };

    private static readonly (string Option, string Value)[] Windowed1080Arguments =
        { ("-screen-fullscreen", "0"), ("-screen-width", "1920"), ("-screen-height", "1080") };

    private static readonly (string Option, string Value)[] BorderlessArguments =
        { ("-screen-fullscreen", "1"), ("-window-mode", "borderless") };

    private static readonly (string Option, string Value)[] FullscreenArguments =
        { ("-screen-fullscreen", "1"), ("-window-mode", "exclusive") };

    /// <summary>Todos los perfiles en el orden en que se muestran (para el desplegable de Ajustes).</summary>
    public static IReadOnlyList<LaunchProfile> All { get; } = new[]
    {
        LaunchProfile.Default,
        LaunchProfile.Windowed720,
        LaunchProfile.Windowed1080,
        LaunchProfile.Borderless,
        LaunchProfile.Fullscreen,
    };

    /// <summary>
    /// Argumentos de Unity de cada perfil (comprobados contra la documentación de Unity 6 "Unity Standalone Player command
    /// line arguments"): Default → ""; Windowed720 → "-screen-fullscreen 0 -screen-width 1280 -screen-height 720";
    /// Windowed1080 → "-screen-fullscreen 0 -screen-width 1920 -screen-height 1080"; Borderless → "-screen-fullscreen 1
    /// -window-mode borderless"; Fullscreen → "-screen-fullscreen 1 -window-mode exclusive". Un valor desconocido → "".
    /// </summary>
    public static string GetArguments(LaunchProfile profile) =>
        string.Join(" ", GetPairs(profile).Select(p => p.Option + " " + p.Value));

    /// <summary>
    /// Argumentos del perfil seguidos de los del usuario (los del usuario al final, así mandan). Espacios normalizados:
    /// saltos de línea, tabuladores y espacios repetidos pasan a un solo espacio, salvo dentro de comillas dobles (rutas).
    /// Si el usuario ya escribe una opción que el perfil también pone (p.ej. "-screen-width 1600"), esa opción del perfil
    /// (con su valor) se omite: así el usuario manda aunque Unity se quede con la primera aparición de cada opción.
    /// </summary>
    public static string Combine(LaunchProfile profile, string? userArguments)
    {
        var userTokens = Tokenize(userArguments);

        // Opciones que el usuario ya escribe (sin distinguir mayúsculas, como Unity).
        var userOptions = new HashSet<string>(
            userTokens.Where(t => t.StartsWith('-')),
            StringComparer.OrdinalIgnoreCase);

        var parts = new List<string>();
        foreach (var (option, value) in GetPairs(profile))
        {
            if (userOptions.Contains(option))
                continue;
            parts.Add(option);
            parts.Add(value);
        }

        parts.AddRange(userTokens);
        return string.Join(" ", parts);
    }

    /// <summary>"Lo que diga el juego", "Ventana 1280×720", "Ventana 1920×1080", "Pantalla completa sin bordes", "Pantalla completa".</summary>
    public static string GetDisplayName(LaunchProfile profile) => profile switch
    {
        LaunchProfile.Windowed720 => "Ventana 1280×720",
        LaunchProfile.Windowed1080 => "Ventana 1920×1080",
        LaunchProfile.Borderless => "Pantalla completa sin bordes",
        LaunchProfile.Fullscreen => "Pantalla completa",
        _ => "Lo que diga el juego",
    };

    private static (string Option, string Value)[] GetPairs(LaunchProfile profile) => profile switch
    {
        LaunchProfile.Windowed720 => Windowed720Arguments,
        LaunchProfile.Windowed1080 => Windowed1080Arguments,
        LaunchProfile.Borderless => BorderlessArguments,
        LaunchProfile.Fullscreen => FullscreenArguments,
        _ => NoArguments,
    };

    /// <summary>
    /// Separa por espacios en blanco (espacio, tabulador, saltos de línea) fuera de comillas dobles. Lo que va entre comillas
    /// se conserva tal cual (comillas incluidas), p.ej. <c>-logFile "C:\Mis  cosas\log.txt"</c> es un solo trozo tras
    /// "-logFile". Unas comillas sin cerrar llegan hasta el final.
    /// </summary>
    internal static List<string> Tokenize(string? arguments)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return tokens;

        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in arguments)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            // Comillas sin cerrar: se quitan los espacios del final para no dejar basura.
            var last = current.ToString().TrimEnd();
            if (last.Length > 0)
                tokens.Add(last);
        }

        return tokens;
    }
}
