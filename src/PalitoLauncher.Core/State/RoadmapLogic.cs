using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.State;

public enum MilestoneStatus
{
    /// <summary>Cerrado en GitHub.</summary>
    Done,

    /// <summary>Abierto y con al menos una tarea cerrada.</summary>
    InProgress,

    /// <summary>El primer milestone abierto sin tareas cerradas que va después de los que están en curso (o el primero abierto si no hay ninguno en curso).</summary>
    Next,

    /// <summary>El resto de milestones abiertos.</summary>
    Planned,
}

/// <summary>Un milestone ya clasificado para la UI.</summary>
public sealed class RoadmapItem
{
    public RoadmapItem(MilestoneInfo milestone, MilestoneStatus status, string phase, string? summary)
    {
        Milestone = milestone;
        Status = status;
        Phase = phase;
        Summary = summary;
    }

    public MilestoneInfo Milestone { get; }
    public MilestoneStatus Status { get; }

    /// <summary>"Pre-producción", "Producción" o "Lanzamiento".</summary>
    public string Phase { get; }

    /// <summary>Frase corta para la lista: el texto tras «Pregunta:» u «Objetivo:» de la descripción (primera frase), o null.</summary>
    public string? Summary { get; }

    /// <summary>"14 de 25 tareas · 56 %" ("Sin tareas todavía" si no tiene; "Completado" si está cerrado).</summary>
    public string ProgressText => RoadmapLogic.FormatProgress(Milestone);
}

public sealed class RoadmapSnapshot
{
    public IReadOnlyList<RoadmapItem> Items { get; init; } = Array.Empty<RoadmapItem>();

    /// <summary>Los que están en curso (pueden ser varios a la vez, p.ej. Pre 1 y Pre 2); si no hay ninguno, el siguiente.</summary>
    public IReadOnlyList<RoadmapItem> Current { get; init; } = Array.Empty<RoadmapItem>();

    public int DoneCount { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>Lógica pura del roadmap (sin E/S), testeable.</summary>
public static class RoadmapLogic
{
    internal const string PhasePreProduction = "Pre-producción";
    internal const string PhaseProduction = "Producción";
    internal const string PhaseLaunch = "Lanzamiento";

    /// <summary>Longitud máxima de <see cref="RoadmapItem.Summary"/> (incluido el «…»).</summary>
    internal const int MaxSummaryLength = 160;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // "Pre 1 · …", "Pre-alpha", "Pre2", "Preproducción", "Pre-production"; no "Presentación" ni "Prestigio".
    private static readonly Regex PreProductionTitle = new(@"^\s*pre(?:\P{L}|$|-?prod)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // Versión al principio del título: "1.0 · Launch", "v2.1 …" → mayor = 1, 2.
    private static readonly Regex VersionTitle = new(@"^\s*v?(\d{1,6})\.\d+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    // «Pregunta:» u «Objetivo:», también en Markdown («**Pregunta:**», «**Objetivo**:»).
    private static readonly Regex SummaryLabel = new(@"\b(?:pregunta|objetivo)\s*[*_]*\s*:[*_]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// Ordena por número y clasifica: Done (cerrado), InProgress (abierto con ClosedIssues &gt; 0), Next (el primer abierto
    /// sin tareas cerradas tras el último en curso; si no hay en curso, el primer abierto) y Planned (el resto).
    /// Fase por título: empieza por "Pre" → "Pre-producción"; versión con mayor ≥ 1 (p.ej. "1.0 · Launch") → "Lanzamiento";
    /// el resto → "Producción". Summary: de la descripción, el texto tras «Pregunta:» u «Objetivo:» hasta el primer punto
    /// final/salto de línea (máx. 160 caracteres, con «…» si se corta); si no hay, null. Current: los InProgress; si no hay,
    /// el Next; si no hay (todo cerrado), lista vacía. Lista null/vacía → snapshot vacío. Nunca lanza.
    /// </summary>
    public static RoadmapSnapshot Build(IEnumerable<MilestoneInfo>? milestones)
    {
        if (milestones is null) return new RoadmapSnapshot();
        try
        {
            // OrderBy es estable: con números repetidos se respeta el orden de GitHub.
            var ordered = milestones.Where(m => m is not null).OrderBy(m => m.Number).ToList();
            if (ordered.Count == 0) return new RoadmapSnapshot();

            var statuses = new MilestoneStatus[ordered.Count];
            var lastInProgress = -1;
            for (var i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                statuses[i] = m.IsClosed ? MilestoneStatus.Done
                    : m.ClosedIssues > 0 ? MilestoneStatus.InProgress
                    : MilestoneStatus.Planned;
                if (statuses[i] == MilestoneStatus.InProgress) lastInProgress = i;
            }

            // Sin ninguno en curso, lastInProgress = -1 y el Next es simplemente el primero abierto.
            for (var i = lastInProgress + 1; i < ordered.Count; i++)
            {
                if (statuses[i] == MilestoneStatus.Planned)
                {
                    statuses[i] = MilestoneStatus.Next;
                    break;
                }
            }

            var items = new List<RoadmapItem>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                items.Add(new RoadmapItem(m, statuses[i], GetPhase(m.Title), ExtractSummary(m.Description)));
            }

            var current = items.Where(it => it.Status == MilestoneStatus.InProgress).ToList();
            if (current.Count == 0)
                current.AddRange(items.Where(it => it.Status == MilestoneStatus.Next));

            return new RoadmapSnapshot
            {
                Items = items.AsReadOnly(),
                Current = current.AsReadOnly(),
                DoneCount = items.Count(it => it.Status == MilestoneStatus.Done),
                TotalCount = items.Count,
            };
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo construir el roadmap a partir de los milestones.", ex);
            return new RoadmapSnapshot();
        }
    }

    /// <summary>"14 de 25 tareas · 56 %"; "1 de 1 tarea · 100 %"; "Sin tareas todavía"; cerrado → "Completado".</summary>
    public static string FormatProgress(MilestoneInfo milestone)
    {
        // Tolerante (se usa desde bindings de la UI): null cuenta como "sin tareas".
        if (milestone is null) return "Sin tareas todavía";
        if (milestone.IsClosed) return "Completado";

        var total = milestone.TotalIssues;
        if (total <= 0) return "Sin tareas todavía";

        var closed = Math.Min(Math.Max(0, milestone.ClosedIssues), total);
        // Redondeo hacia abajo con enteros: nunca muestra "100 %" si queda alguna tarea abierta.
        var percent = (int)((long)closed * 100 / total);
        var noun = total == 1 ? "tarea" : "tareas";
        return string.Create(CultureInfo.InvariantCulture, $"{closed} de {total} {noun} · {percent} %");
    }

    /// <summary>Fase según el título del milestone (ver <see cref="Build"/>).</summary>
    internal static string GetPhase(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return PhaseProduction;
        try
        {
            if (PreProductionTitle.IsMatch(title)) return PhasePreProduction;

            var version = VersionTitle.Match(title);
            if (version.Success && int.TryParse(version.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                && major >= 1)
                return PhaseLaunch;
        }
        catch (RegexMatchTimeoutException ex)
        {
            Log.Warn($"Título de milestone demasiado complejo para clasificarlo: \"{title}\"", ex);
        }
        return PhaseProduction;
    }

    /// <summary>
    /// Texto tras «Pregunta:» u «Objetivo:» hasta el primer salto de línea o punto final (incluido), sin marcas de
    /// Markdown (**, __, `), con espacios normalizados, la primera letra en mayúscula y como mucho
    /// <see cref="MaxSummaryLength"/> caracteres (cortado por palabra con «…»). Null si no hay etiqueta o queda vacío.
    /// </summary>
    internal static string? ExtractSummary(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        try
        {
            var label = SummaryLabel.Match(description);
            if (!label.Success) return null;

            var rest = description.AsSpan(label.Index + label.Length).TrimStart(" \t");
            // «Pregunta:» sola en su línea: se usa la línea siguiente (si está vacía, no hay resumen).
            if (rest.StartsWith("\r\n")) rest = rest[2..];
            else if (rest.Length > 0 && rest[0] is '\r' or '\n') rest = rest[1..];
            var lineEnd = rest.IndexOfAny('\r', '\n');
            var line = (lineEnd >= 0 ? rest[..lineEnd] : rest).ToString();

            line = line.Replace("**", "", StringComparison.Ordinal)
                .Replace("__", "", StringComparison.Ordinal)
                .Replace("`", "", StringComparison.Ordinal);
            line = CollapseWhitespace(line);
            line = CutAtSentenceEnd(line).Trim().TrimStart('*', '_').TrimEnd('*', '_').Trim();
            if (line.Length == 0) return null;

            line = CapitalizeFirstLetter(line);
            return Truncate(line, MaxSummaryLength);
        }
        catch (RegexMatchTimeoutException ex)
        {
            Log.Warn("Descripción de milestone demasiado compleja para extraer el resumen.", ex);
            return null;
        }
    }

    /// <summary>Corta tras el primer "." que cierra frase (seguido de espacio o fin), no en "0.1" ni en "v1.2".</summary>
    private static string CutAtSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '.') continue;
            if (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])) return text[..(i + 1)];
        }
        return text;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>"¿pueden 2-4 jugadores…?" → "¿Pueden 2-4 jugadores…?" (se salta la puntuación inicial ¿ ¡ « " ().</summary>
    private static string CapitalizeFirstLetter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetter(c))
            {
                if (!char.IsLower(c)) return text;
                return string.Concat(text.AsSpan(0, i), char.ToUpper(c, CultureInfo.InvariantCulture).ToString(), text.AsSpan(i + 1));
            }
            if (char.IsLetterOrDigit(c)) return text;
        }
        return text;
    }

    /// <summary>Recorta a <paramref name="max"/> caracteres en total (con «…»), por palabra si es razonable.</summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = max - 1; // hueco para «…»
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // no partir un emoji
        var space = text.LastIndexOf(' ', cut);
        if (space >= max / 2) cut = space;

        var head = text[..cut].TrimEnd(' ', ',', ';', ':', '.', '-', '·');
        return head + "…";
    }
}
