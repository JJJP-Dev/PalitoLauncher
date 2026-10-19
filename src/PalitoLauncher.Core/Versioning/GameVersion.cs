using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Versioning;

/// <summary>
/// Versión comparable extraída de nombres de assets / tags / títulos de release.
/// Formatos soportados (en cualquier parte del texto, sin distinguir mayúsculas):
///   "2D_0.0.2_Protype3.zip"   → 0.0.2, prerelease prototype.3
///   "0.0.2-Prototype-2"       → 0.0.2, prerelease prototype.2
///   "2D_0.1.0.zip"            → 0.1.0 (release estable)
///   "v1.2.3", "1.2", "1.2.3.4", "1.0.0-beta.2", "1.0.0 RC1", "0.3.0_alpha"
/// Reglas de parseo:
///   1. Se quita una extensión final .zip/.rar/.7z/.exe antes de parsear (así "zip" nunca es una etiqueta).
///   2. Núcleo numérico de 2 a 4 componentes con límites de dígito: (?&lt;![\d.])v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?![\d])
///      → "2D_" nunca produce versión; "2D_1.zip" no tiene versión.
///   3. Etiqueta = la primera racha de letras justo tras el núcleo (separador opcional [-_. +]); número = dígitos opcionales
///      tras ella (separador opcional [-_. ]).
/// Etiquetas (normalizadas en <see cref="PreLabel"/>):
///   cualquier palabra que empiece por "prot" (proto, prototype, protype, prototipe, prototyp, protoype, prototipo) → "prototype";
///   alpha / alfa → "alpha";  beta → "beta";  rc → "rc";  preview / pre → "preview";  dev → "dev".
///   Palabras que NO son etiqueta (versión estable): release, final, stable, build, full, setup, client, server, win,
///   windows, x (de "x64"), linux, mac, macos, osx, steam, itch, game, coop, launcher.
///   Cualquier otra etiqueta DESCONOCIDA sigue siendo prerelease (nunca estable): se guarda en minúsculas tal cual, con el
///   mismo rango que "prototype", y se registra un Log.Warn.
/// Orden (semver): mismo núcleo numérico → prerelease &lt; release. Entre prereleases: rango de etiqueta
/// (dev &lt; prototype = desconocidas &lt; alpha &lt; beta &lt; preview &lt; rc; empate de rango → orden ordinal de la etiqueta)
/// y luego número (sin número &lt; con número).
/// Invariante: TryParse(v.ToString()) == v.
/// Inmutable. Igualdad y hash coherentes con <see cref="CompareTo"/>.
/// </summary>
public sealed class GameVersion : IComparable<GameVersion>, IEquatable<GameVersion>
{
    private const string Prototype = "prototype";
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string Rc = "rc";
    private const string Preview = "preview";
    private const string Dev = "dev";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Extensión de archivo final que nunca debe interpretarse como etiqueta ni como componente.
    private static readonly Regex ExtensionRegex = new(@"\.(zip|rar|7z|exe)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    // Núcleo numérico (se usa [0-9] en vez de \d para no aceptar dígitos Unicode que int.Parse no entiende).
    private static readonly Regex CoreRegex = new(
        @"(?<![0-9.])v?([0-9]+)\.([0-9]+)(?:\.([0-9]+))?(?:\.([0-9]+))?(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    // Etiqueta justo tras el núcleo: separador opcional, racha de letras y número opcional.
    private static readonly Regex LabelRegex = new(@"\G[-_. +]?(\p{L}+)(?:[-_. ]?([0-9]+))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    private static readonly HashSet<string> IgnoredWords = new(StringComparer.Ordinal)
    {
        "release", "final", "stable", "build", "full", "setup", "client", "server", "win", "windows", "x",
        "linux", "mac", "macos", "osx", "steam", "itch", "game", "coop", "launcher",
    };

    // Etiquetas desconocidas ya avisadas en el log (para no repetir el aviso en cada parseo).
    private static readonly ConcurrentDictionary<string, byte> WarnedUnknownLabels = new(StringComparer.Ordinal);

    public GameVersion(int major, int minor, int patch, int revision = 0, string? preLabel = null, int? preNumber = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (preNumber is < 0)
            throw new ArgumentOutOfRangeException(nameof(preNumber), preNumber, "El número de prerelease no puede ser negativo.");

        string? label = null;
        if (!string.IsNullOrWhiteSpace(preLabel))
        {
            var trimmed = preLabel.Trim();
            // Solo letras: garantiza que ToString() se pueda volver a parsear igual.
            foreach (var c in trimmed)
            {
                if (!char.IsLetter(c))
                    throw new ArgumentException("La etiqueta de prerelease solo puede contener letras.", nameof(preLabel));
            }
            label = NormalizeLabel(trimmed.ToLowerInvariant(), out _);
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
        PreLabel = label;
        PreNumber = label is null ? null : preNumber;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int Revision { get; }

    /// <summary>Etiqueta normalizada en minúsculas ("prototype", "alpha", ...) o null si es estable.</summary>
    public string? PreLabel { get; }

    /// <summary>Número de la prerelease (Prototipo 3 → 3) o null.</summary>
    public int? PreNumber { get; }

    public bool IsPrerelease => PreLabel != null;

    /// <summary>Busca la primera versión dentro del texto. Devuelve false si no hay ninguna.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out GameVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var input = ExtensionRegex.Replace(text.Trim(), "");
            for (var core = CoreRegex.Match(input); core.Success; core = core.NextMatch())
            {
                // Componentes que no caben en int → se ignora esta coincidencia y se busca la siguiente.
                if (!TryParseComponent(core.Groups[1], out var major) || !TryParseComponent(core.Groups[2], out var minor)
                    || !TryParseComponent(core.Groups[3], out var patch) || !TryParseComponent(core.Groups[4], out var revision))
                {
                    continue;
                }

                string? label = null;
                int? number = null;
                var labelMatch = LabelRegex.Match(input, core.Index + core.Length);
                if (labelMatch.Success)
                {
                    var raw = labelMatch.Groups[1].Value.ToLowerInvariant();
                    label = NormalizeLabel(raw, out var unknown);
                    if (label != null && labelMatch.Groups[2].Success
                        && int.TryParse(labelMatch.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    {
                        number = n;
                    }

                    if (unknown && WarnedUnknownLabels.TryAdd(raw, 0))
                        Log.Warn($"Etiqueta de versión desconocida \"{raw}\" en \"{text}\": se trata como versión de prueba.");
                }

                version = new GameVersion(major, minor, patch, revision, label, number);
                return true;
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            Log.Warn($"Tiempo agotado al buscar una versión en \"{text}\".", ex);
        }

        return false;
    }

    /// <summary>Intenta, en orden: nombre del asset, tag, título de la release. Null si ninguno tiene versión.</summary>
    public static GameVersion? FromRelease(string? assetName, string? tagName, string? releaseName)
    {
        if (TryParse(assetName, out var v)) return v;
        if (TryParse(tagName, out v)) return v;
        if (TryParse(releaseName, out v)) return v;
        return null;
    }

    /// <summary>Texto para la UI: "0.0.2 · Prototipo 3", "0.1.0", "1.0.0 · Beta 2", "1.0.0 · RC 1", "1.2.3.4".</summary>
    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        AppendCore(sb);
        if (PreLabel != null)
        {
            sb.Append(" · ").Append(GetLabelDisplayName(PreLabel));
            if (PreNumber is { } n)
                sb.Append(' ').Append(n.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Forma canónica estilo semver: "0.0.2-prototype.3", "0.1.0", "1.2.3.4-beta". Revision solo si != 0.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        AppendCore(sb);
        if (PreLabel != null)
        {
            sb.Append('-').Append(PreLabel);
            if (PreNumber is { } n)
                sb.Append('.').Append(n.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>null se ordena como la versión más baja.</summary>
    public int CompareTo(GameVersion? other)
    {
        if (other is null) return 1;
        if (ReferenceEquals(this, other)) return 0;

        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;

        // Mismo núcleo: la estable va después de cualquier prerelease.
        if (PreLabel is null) return other.PreLabel is null ? 0 : 1;
        if (other.PreLabel is null) return -1;

        c = GetLabelRank(PreLabel).CompareTo(GetLabelRank(other.PreLabel));
        if (c != 0) return c;
        c = string.CompareOrdinal(PreLabel, other.PreLabel);
        if (c != 0) return Math.Sign(c);

        // Sin número < con número.
        if (PreNumber is null) return other.PreNumber is null ? 0 : -1;
        if (other.PreNumber is null) return 1;
        return PreNumber.Value.CompareTo(other.PreNumber.Value);
    }

    public bool Equals(GameVersion? other)
        => other is not null
           && Major == other.Major && Minor == other.Minor && Patch == other.Patch && Revision == other.Revision
           && string.Equals(PreLabel, other.PreLabel, StringComparison.Ordinal)
           && PreNumber == other.PreNumber;

    public override bool Equals(object? obj) => obj is GameVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Revision, PreLabel, PreNumber);

    public static bool operator ==(GameVersion? a, GameVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(GameVersion? a, GameVersion? b) => !(a == b);
    public static bool operator <(GameVersion? a, GameVersion? b) => Compare(a, b) < 0;
    public static bool operator >(GameVersion? a, GameVersion? b) => Compare(a, b) > 0;
    public static bool operator <=(GameVersion? a, GameVersion? b) => Compare(a, b) <= 0;
    public static bool operator >=(GameVersion? a, GameVersion? b) => Compare(a, b) >= 0;

    /// <summary>Comparación tolerante a null (null &lt; cualquier versión).</summary>
    public static int Compare(GameVersion? a, GameVersion? b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b);

    private void AppendCore(StringBuilder sb)
    {
        sb.Append(Major.ToString(CultureInfo.InvariantCulture)).Append('.')
          .Append(Minor.ToString(CultureInfo.InvariantCulture)).Append('.')
          .Append(Patch.ToString(CultureInfo.InvariantCulture));
        if (Revision != 0)
            sb.Append('.').Append(Revision.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryParseComponent(Group group, out int value)
    {
        value = 0;
        return !group.Success || int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Normaliza una palabra (ya en minúsculas) a etiqueta. Null = no es etiqueta (versión estable).
    /// <paramref name="unknown"/> = true si es una etiqueta no reconocida (se conserva tal cual).
    /// </summary>
    private static string? NormalizeLabel(string word, out bool unknown)
    {
        unknown = false;
        if (word.StartsWith("prot", StringComparison.Ordinal)) return Prototype;
        switch (word)
        {
            case "alpha" or "alfa": return Alpha;
            case "beta": return Beta;
            case "rc": return Rc;
            case "preview" or "pre": return Preview;
            case "dev": return Dev;
        }
        if (IgnoredWords.Contains(word)) return null;
        unknown = true;
        return word;
    }

    // dev < prototype = desconocidas < alpha < beta < preview < rc
    private static int GetLabelRank(string label) => label switch
    {
        Dev => 0,
        Alpha => 2,
        Beta => 3,
        Preview => 4,
        Rc => 5,
        _ => 1, // prototype y desconocidas
    };

    private static string GetLabelDisplayName(string label) => label switch
    {
        Prototype => "Prototipo",
        Alpha => "Alpha",
        Beta => "Beta",
        Rc => "RC",
        Preview => "Preview",
        Dev => "Dev",
        _ => label,
    };
}
