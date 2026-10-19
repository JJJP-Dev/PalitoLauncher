using System.Collections.ObjectModel;
using System.Windows.Input;
using PalitoLauncher.Core;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.State;
using PalitoLauncher.Helpers;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

// Roadmap: milestones del repo del juego (página Roadmap y tarjeta "En desarrollo" de Inicio).
public sealed partial class MainViewModel
{
    /// <summary>Hitos "en desarrollo" que se muestran en Inicio como mucho.</summary>
    private const int MaxHomeRoadmapItems = 2;

    /// <summary>Sin datos, al volver a la página se reintenta como mucho con esta frecuencia.</summary>
    private static readonly TimeSpan RoadmapRetryInterval = TimeSpan.FromMinutes(1);

    private MilestoneFetchResult? _roadmapResult;
    private RoadmapSnapshot? _roadmapSnapshot;
    private bool _roadmapLoading;
    private bool _roadmapAttempted;
    private bool _roadmapDeferredByRateLimit;
    private bool _roadmapDeferredCacheTried; // con pocas consultas ya se intentó (una vez) cargar la copia guardada
    private DateTimeOffset? _roadmapLastAttemptUtc;

    // Presentación.
    private bool _hasRoadmapItems;
    private bool _hasRoadmapCurrent;
    private bool _isRoadmapLoading = true;
    private bool _isRoadmapFailed;
    private bool _isRoadmapEmpty;
    private bool _isRoadmapStale;
    private string _roadmapSummaryText = "";
    private double _roadmapDoneValue;
    private string _roadmapStatusText = "";
    private string _roadmapFailedText = RoadmapFailedMessage;

    private const string RoadmapFailedMessage = "No se pudo cargar el roadmap. Revisa tu conexión.";
    private const string RoadmapDeferredMessage =
        "GitHub limita las consultas y ahora quedan pocas: el roadmap se cargará más tarde. Pulsa Reintentar para cargarlo ya.";

    // ───────────── Propiedades para las vistas ─────────────

    public bool IsRoadmapPage => _currentPage == AppPage.Roadmap;

    /// <summary>Fases con sus hitos (página Roadmap).</summary>
    public ObservableCollection<RoadmapPhaseViewModel> RoadmapPhases { get; } = new();

    /// <summary>Todos los hitos en orden (barra global segmentada).</summary>
    public ObservableCollection<RoadmapItemViewModel> RoadmapItems { get; } = new();

    /// <summary>Los que están en curso (o el siguiente), como mucho 2 (tarjeta de Inicio).</summary>
    public ObservableCollection<RoadmapItemViewModel> RoadmapCurrent { get; } = new();

    public bool HasRoadmapItems { get => _hasRoadmapItems; private set => SetProperty(ref _hasRoadmapItems, value); }

    /// <summary>La tarjeta "En desarrollo" de Inicio solo se ve si hay algo que enseñar.</summary>
    public bool HasRoadmapCurrent { get => _hasRoadmapCurrent; private set => SetProperty(ref _hasRoadmapCurrent, value); }

    /// <summary>Aún no hay datos y se están pidiendo (o todavía no se han pedido).</summary>
    public bool IsRoadmapLoading { get => _isRoadmapLoading; private set => SetProperty(ref _isRoadmapLoading, value); }

    /// <summary>Se intentó y no hay datos (ni en caché).</summary>
    public bool IsRoadmapFailed { get => _isRoadmapFailed; private set => SetProperty(ref _isRoadmapFailed, value); }

    /// <summary>GitHub respondió pero el repo no tiene milestones.</summary>
    public bool IsRoadmapEmpty { get => _isRoadmapEmpty; private set => SetProperty(ref _isRoadmapEmpty, value); }

    /// <summary>Los datos son de la caché porque no se pudo consultar GitHub.</summary>
    public bool IsRoadmapStale { get => _isRoadmapStale; private set => SetProperty(ref _isRoadmapStale, value); }

    /// <summary>"3 de 14 hitos completados".</summary>
    public string RoadmapSummaryText { get => _roadmapSummaryText; private set => SetProperty(ref _roadmapSummaryText, value); }

    /// <summary>0..100 (hitos completados / totales).</summary>
    public double RoadmapDoneValue { get => _roadmapDoneValue; private set => SetProperty(ref _roadmapDoneValue, value); }

    /// <summary>"Comprobado a las 14:32" / el aviso de GitHub con datos guardados (o "Sin conexión · datos guardados") / "Consultando GitHub…".</summary>
    public string RoadmapStatusText { get => _roadmapStatusText; private set => SetProperty(ref _roadmapStatusText, value); }

    public string RoadmapFailedText { get => _roadmapFailedText; private set => SetProperty(ref _roadmapFailedText, value); }

    public ICommand RefreshRoadmapCommand { get; }
    public ICommand OpenRoadmapOnGitHubCommand { get; }
    public ICommand ShowRoadmapCommand { get; }

    // ───────────── Carga ─────────────

    /// <summary>
    /// Pide los milestones (con la caché/frescura de 10 min del GitHubClient salvo <paramref name="bypassFreshness"/>).
    /// <paramref name="automatic"/>: arranque, timer o al abrir la página; respeta la reserva de consultas de GitHub. Nunca
    /// muestra diálogos: los errores van al log. Devuelve lo que trajo ESTA llamada (de red o de caché), o null si no
    /// trajo nada o no llegó a pedirse.
    /// </summary>
    private async Task<MilestoneFetchResult?> RefreshRoadmapAsync(bool bypassFreshness, bool automatic)
    {
        if (_roadmapLoading || _disposed) return null;

        if (automatic)
        {
            // Con el cupo ya agotado (0) el GitHubClient no pregunta y devuelve lo guardado: se deja pasar.
            var remaining = RateLimitRemainingSafe();
            if (remaining is int r && r > 0 && r <= LauncherConfig.RateLimitReserveForUser)
            {
                // Quedan pocas consultas: no se pregunta a GitHub... salvo que aún no haya nada que enseñar (p.ej. recién
                // abierto el launcher). Entonces se pide UNA vez por sesión sin saltarse la frescura: si la copia guardada
                // tiene menos de 10 min, el GitHubClient la devuelve sin consultar (lo normal al reabrir el launcher); si
                // es más vieja o no existe, gasta como mucho esa 1 consulta (con If-None-Match). El GitHubClient (Core) no
                // tiene un modo "solo caché", así que este es el mínimo posible con su API.
                if (_roadmapSnapshot != null || _roadmapDeferredCacheTried)
                {
                    Log.Info($"Roadmap: consulta omitida, quedan {r} consultas a GitHub.");
                    _roadmapDeferredByRateLimit = true;
                    _roadmapAttempted = true;
                    UpdateRoadmapPresentation();
                    return null;
                }

                _roadmapDeferredCacheTried = true;
                bypassFreshness = false;
                Log.Info($"Roadmap: quedan {r} consultas a GitHub; se usa la copia guardada si es reciente.");
            }
        }

        _roadmapLoading = true;
        _roadmapDeferredByRateLimit = false;
        UpdateRoadmapPresentation();

        MilestoneFetchResult? result = null;
        try
        {
            result = await _github.GetMilestonesAsync(LauncherConfig.GameRepoOwner, LauncherConfig.GameRepoName, _lifetimeCts.Token,
                bypassFreshness);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Cerrando.
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al consultar los milestones.", ex);
        }
        finally
        {
            _roadmapLoading = false;
            _roadmapAttempted = true;
            _roadmapLastAttemptUtc = DateTimeOffset.UtcNow;
        }

        if (_disposed) return null;
        if (result != null) ApplyRoadmapResult(result);
        else Log.Warn("No se pudo obtener el roadmap (sin datos guardados).");

        UpdateRoadmapPresentation();
        return result;
    }

    /// <summary>
    /// «Actualizar» (y «Reintentar») de la página Roadmap: pregunta a GitHub saltándose la frescura (gasta 1 consulta),
    /// salvo que se acabe de comprobar (como «Actualizar lista» de Versiones) o que queden pocas consultas (entonces sin
    /// saltarse la frescura: una copia guardada reciente no gasta nada). El toast dice lo que pasó de verdad.
    /// </summary>
    private Task RefreshRoadmapManuallyAsync() => Guarded(async () =>
    {
        if (_roadmapLoading) return;
        if (_roadmapResult is { IsStale: false, FetchedAt: { } fetchedAt }
            && DateTimeOffset.UtcNow - fetchedAt < LauncherConfig.ApiFreshnessWindow)
        {
            ShowToast("Comprobado hace un momento.");
            return;
        }

        bool bypass = !_roadmapDeferredByRateLimit;
        var result = await RefreshRoadmapAsync(bypassFreshness: bypass, automatic: false);
        ShowToast(RoadmapRefreshToast(result));
    }, "Actualizar roadmap");

    /// <summary>Toast de «Actualizar»: el motivo real (aviso de GitHub, límite de consultas) en vez de "Revisa tu conexión".</summary>
    private string RoadmapRefreshToast(MilestoneFetchResult? result)
    {
        if (result == null)
        {
            if (RoadmapRateLimitedMessage() is { } limited) return limited;
            return _roadmapSnapshot == null ? RoadmapFailedMessage : "No se pudo actualizar el roadmap. Revisa tu conexión.";
        }
        if (result.IsStale)
            return string.IsNullOrWhiteSpace(result.Warning) ? "Sin conexión con GitHub: se muestran los datos guardados." : result.Warning.Trim();
        return "Roadmap actualizado.";
    }

    /// <summary>"GitHub limita las consultas; vuelve a intentarlo a las HH:mm" si ahora mismo no se puede consultar.</summary>
    private string? RoadmapRateLimitedMessage()
    {
        if (!IsRateLimitedSafe()) return null;
        DateTimeOffset? reset;
        try
        {
            reset = _github.RateLimitResetUtc;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer cuándo se restablece el límite de GitHub.", ex);
            reset = null;
        }
        return reset is { } at
            ? $"GitHub limita las consultas; vuelve a intentarlo a las {UiFormat.Time(at)}."
            : "GitHub limita las consultas; vuelve a intentarlo más tarde.";
    }

    /// <summary>
    /// Al abrir la página: si los datos son viejos se piden (sin saltarse la frescura ni la reserva); si no hay datos, se
    /// reintenta como mucho una vez por minuto (entrar y salir de la página no debe gastar consultas).
    /// </summary>
    private void RefreshRoadmapIfStale()
    {
        if (_roadmapLoading || !_initialized) return;
        var sinceLast = _roadmapLastAttemptUtc is { } last ? DateTimeOffset.UtcNow - last : TimeSpan.MaxValue;
        bool old = sinceLast > LauncherConfig.MilestonesFreshnessWindow;
        bool retryMissing = _roadmapSnapshot == null && sinceLast > RoadmapRetryInterval;
        if (old || retryMissing)
            RunFlow(() => RefreshRoadmapAsync(bypassFreshness: false, automatic: true), "Roadmap al abrir la página", showErrors: false);
    }

    /// <summary>Aplica una respuesta de /milestones (clasifica con RoadmapLogic y reconstruye las listas).</summary>
    internal void ApplyRoadmapResult(MilestoneFetchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _roadmapResult = result;

        RoadmapSnapshot snapshot;
        try
        {
            snapshot = RoadmapLogic.Build(result.Milestones);
        }
        catch (Exception ex)
        {
            Log.Error("Error al clasificar los milestones del roadmap.", ex);
            snapshot = new RoadmapSnapshot();
        }

        _roadmapSnapshot = snapshot;
        RebuildRoadmapItems(snapshot);
        Log.Info($"Roadmap: {snapshot.TotalCount} hitos, {snapshot.DoneCount} completados (origen {result.Source}).");
        UpdateRoadmapPresentation();
    }

    /// <summary>Reconstruye fases, barra global y tarjeta de Inicio (conserva los hitos expandidos).</summary>
    private void RebuildRoadmapItems(RoadmapSnapshot snapshot)
    {
        var expanded = RoadmapItems.Where(i => i.IsExpanded).Select(i => i.Number).ToHashSet();

        RoadmapPhases.Clear();
        RoadmapItems.Clear();
        RoadmapCurrent.Clear();

        var vmByItem = new Dictionary<RoadmapItem, RoadmapItemViewModel>(ReferenceEqualityComparer.Instance);
        // Fases en el orden en que aparecen sus hitos (el roadmap ya viene ordenado por número).
        foreach (var group in snapshot.Items.GroupBy(i => i.Phase ?? ""))
        {
            var items = new List<RoadmapItemViewModel>();
            foreach (var item in group)
            {
                var vm = new RoadmapItemViewModel(item, showSeparator: items.Count > 0);
                if (expanded.Contains(vm.Number)) vm.IsExpanded = true;
                items.Add(vm);
                vmByItem[item] = vm;
            }
            RoadmapPhases.Add(new RoadmapPhaseViewModel(group.Key, items));
        }

        foreach (var item in snapshot.Items)
            if (vmByItem.TryGetValue(item, out var vm)) RoadmapItems.Add(vm);

        foreach (var item in snapshot.Current.Take(MaxHomeRoadmapItems))
            RoadmapCurrent.Add(vmByItem.TryGetValue(item, out var vm) ? vm : new RoadmapItemViewModel(item, showSeparator: false));
    }

    private void UpdateRoadmapPresentation()
    {
        if (_disposed) return;
        var snapshot = _roadmapSnapshot;
        bool hasData = snapshot != null;

        IsRoadmapFailed = !hasData && _roadmapAttempted && !_roadmapLoading;
        IsRoadmapLoading = !hasData && !IsRoadmapFailed;
        IsRoadmapEmpty = hasData && snapshot!.TotalCount == 0;
        HasRoadmapItems = hasData && snapshot!.TotalCount > 0;
        HasRoadmapCurrent = HasRoadmapItems && RoadmapCurrent.Count > 0;
        RoadmapFailedText = _roadmapDeferredByRateLimit
            ? RoadmapDeferredMessage
            : IsRoadmapFailed ? RoadmapRateLimitedMessage() ?? RoadmapFailedMessage : RoadmapFailedMessage;

        if (snapshot is { TotalCount: > 0 })
        {
            int total = snapshot.TotalCount;
            int done = Math.Clamp(snapshot.DoneCount, 0, total);
            RoadmapSummaryText = total == 1 ? $"{done} de 1 hito completado" : $"{done} de {total} hitos completados";
            RoadmapDoneValue = Math.Round(100.0 * done / total, 1);
        }
        else
        {
            RoadmapSummaryText = "";
            RoadmapDoneValue = 0;
        }

        IsRoadmapStale = _roadmapResult?.IsStale == true;
        if (_roadmapLoading && hasData)
            RoadmapStatusText = "Consultando GitHub…";
        else if (_roadmapResult == null)
            RoadmapStatusText = "";
        else if (_roadmapResult.IsStale)
            // El motivo real (sin conexión, límite de consultas, repo no encontrado…) en vez de "Sin conexión" para todo.
            RoadmapStatusText = string.IsNullOrWhiteSpace(_roadmapResult.Warning) ? "Sin conexión · datos guardados" : _roadmapResult.Warning.Trim();
        else
            RoadmapStatusText = $"Comprobado a las {UiFormat.Time(_roadmapResult.FetchedAt ?? _roadmapLastAttemptUtc ?? DateTimeOffset.UtcNow)}";

        CommandManager.InvalidateRequerySuggested();
    }

    private void OpenRoadmapOnGitHub()
    {
        if (!ShellService.OpenUrl(LauncherConfig.GameMilestonesPageUrl)) ShowToast("No se pudo abrir el navegador.");
    }

    /// <summary>Resumen del roadmap para el informe del desarrollador.</summary>
    private string RoadmapDiagnosticText()
    {
        var snapshot = _roadmapSnapshot;
        if (snapshot == null) return _roadmapAttempted ? "(sin datos)" : "(no consultado)";
        return $"{snapshot.DoneCount}/{snapshot.TotalCount} hitos (origen {_roadmapResult?.Source.ToString() ?? "-"})";
    }
}
