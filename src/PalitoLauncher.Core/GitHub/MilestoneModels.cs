namespace PalitoLauncher.Core.GitHub;

/// <summary>Milestone de GitHub (subset de campos que usa el roadmap).</summary>
public sealed class MilestoneInfo
{
    /// <summary>Número del milestone en GitHub (en Coop2D sigue el orden del roadmap: 1 = Pre 1 … 14 = 1.0).</summary>
    public int Number { get; init; }

    public string Title { get; init; } = "";

    /// <summary>Descripción en Markdown (en Coop2D empieza por la fase y lleva «Pregunta:» u «Objetivo:»).</summary>
    public string? Description { get; init; }

    /// <summary>True si el milestone está cerrado en GitHub (state == "closed").</summary>
    public bool IsClosed { get; init; }

    public int OpenIssues { get; init; }
    public int ClosedIssues { get; init; }
    public DateTimeOffset? DueOn { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string HtmlUrl { get; init; } = "";

    public int TotalIssues => Math.Max(0, OpenIssues) + Math.Max(0, ClosedIssues);

    /// <summary>0..1 (tareas cerradas / totales), o null si no tiene ninguna tarea.</summary>
    public double? Progress => TotalIssues > 0 ? Math.Clamp((double)Math.Max(0, ClosedIssues) / TotalIssues, 0, 1) : null;
}

public sealed class MilestoneFetchResult
{
    /// <summary>Milestones ordenados por <see cref="MilestoneInfo.Number"/> ascendente.</summary>
    public IReadOnlyList<MilestoneInfo> Milestones { get; init; } = Array.Empty<MilestoneInfo>();

    public ReleaseFetchSource Source { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>Aviso en español cuando Source == OfflineCache.</summary>
    public string? Warning { get; init; }

    public bool IsStale => Source == ReleaseFetchSource.OfflineCache;
}
