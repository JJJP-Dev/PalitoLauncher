using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Missions;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Helpers;

namespace PalitoLauncher.ViewModels;

// Misiones de prueba (launcher/missions.json + eventos del juego en Player.log). Diseño: docs/MISIONES.md.
public sealed partial class MainViewModel
{
    /// <summary>Tras cerrarse el juego, cuánto se espera a que Unity termine de escribir Player.log.</summary>
    private static readonly TimeSpan MissionLogSettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>Como mucho un envío automático a Discord cada tanto (las marcas seguidas se agrupan).</summary>
    private static readonly TimeSpan MissionSendThrottle = TimeSpan.FromMinutes(2);

    /// <summary>Espera mínima tras una marca del jugador antes de enviar (por si marca varias seguidas).</summary>
    private static readonly TimeSpan MissionSendDebounce = TimeSpan.FromSeconds(20);

    private readonly MissionsProvider? _missionsProvider;
    private readonly MissionStateStore? _missionStore;
    private readonly MissionReportService? _missionReports;

    private MissionSet? _missionSet;
    private MissionSetState? _missionState;
    private int _missionRevision;
    private bool _missionSendRunning;
    private bool _missionLogsRunning;
    private DateTimeOffset? _missionSendBlockedUntil;
    private DispatcherTimer? _missionSendTimer;
    private MissionSendStatus? _lastMissionSendStatus;

    /// <summary>Tarjeta «Misiones de prueba» de Inicio.</summary>
    public MissionsCardViewModel Missions { get; }

    /// <summary>Ajustes › «Enviar resultados ahora» (y botón de la tarjeta).</summary>
    public ICommand SendMissionResultsCommand { get; }

    /// <summary>Rutas de Player.log y Player-prev.log del juego instalado. Los tests las sustituyen.</summary>
    internal Func<Task<(string? PlayerLog, string? PlayerPrevLog)>>? MissionLogPathsOverride { get; set; }

    /// <summary>Nunca impide abrir el launcher: si no se puede crear, simplemente no hay misiones.</summary>
    private static MissionsProvider? CreateMissionsProvider(Core.GitHub.GitHubClient github)
    {
        try
        {
            return new MissionsProvider(github, AppPaths.AppDataDir);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar la lectura de missions.json; no habrá misiones de prueba.", ex);
            return null;
        }
    }

    private static MissionStateStore? CreateMissionStore()
    {
        try
        {
            return new MissionStateStore(AppPaths.MissionsDir);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar el estado de las misiones.", ex);
            return null;
        }
    }

    private static MissionReportService? CreateMissionReportService()
    {
        try
        {
            return new MissionReportService(HttpFactory.Shared);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar el envío de las misiones; los resultados se quedarán en este PC.", ex);
            return null;
        }
    }

    // ───────────── Lectura de missions.json ─────────────

    /// <summary>Al arrancar y cada 15 min (con launcher.json): lee el conjunto, procesa los logs pendientes y envía si toca.</summary>
    private async Task RefreshMissionsAsync()
    {
        if (_missionsProvider == null || _disposed) return;
        MissionSet? set;
        try
        {
            set = await _missionsProvider.GetMissionSetAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed) return;

        MissionSetState? state = null;
        if (set != null)
        {
            if (_missionState != null && string.Equals(_missionState.SetId, set.Id, StringComparison.OrdinalIgnoreCase))
            {
                state = _missionState;
            }
            else if (_missionStore != null)
            {
                var store = _missionStore;
                var now = UtcNow();
                state = await Task.Run(() => store.LoadOrCreate(set.Id, now));
                if (_disposed) return;
            }
            else
            {
                state = new MissionSetState { SetId = set.Id, CreatedUtc = UtcNow() };
            }
        }

        bool isNewSet = state != null && !ReferenceEquals(state, _missionState);
        ApplyMissionSet(set, state);
        if (state != null && isNewSet && state.ProcessedSessions.Count == 0 && state.SessionsCounted == 0)
            await SaveMissionStateAsync(); // recuerda desde cuándo existe el conjunto (las partidas anteriores no cuentan)

        await ProcessMissionLogsAsync(afterGame: false);
        await MaybeSendMissionResultsAsync(force: false);
    }

    /// <summary>Muestra (o quita) el conjunto. Barato; nunca lanza.</summary>
    internal void ApplyMissionSet(MissionSet? set, MissionSetState? state)
    {
        if (_disposed) return;
        if (set == null || state == null)
        {
            if (_missionSet != null) Log.Info("Misiones: ya no hay conjunto activo.");
            _missionSet = null;
            _missionState = null;
            Missions.Clear();
            UpdateMissionSettings();
            return;
        }

        if (_missionSet == null || !string.Equals(_missionSet.Id, set.Id, StringComparison.Ordinal))
            Log.Info($"Misiones: conjunto «{set.Id}» ({set.Missions.Count} misiones, {state.Missions.Values.Count(m => m.State == MissionState.Done)} completadas).");
        _missionSet = set;
        _missionState = state;
        foreach (var mission in set.Missions) state.GetOrCreate(mission);
        RefreshMissionsCard();
    }

    /// <summary>Tarjeta, aviso de versión y estado del envío en Ajustes.</summary>
    private void RefreshMissionsCard()
    {
        try
        {
            var set = _missionSet;
            var state = _missionState;
            if (set == null || state == null)
            {
                Missions.Clear();
            }
            else
            {
                Missions.Apply(set, state);
                Missions.VersionWarning = MissionVersionWarning(set);
                Missions.IsVisible = _settings.ShowMissions && set.IsActive(UtcNow());
            }
        }
        catch (Exception ex)
        {
            LogManifestError("mostrar las misiones", ex);
        }
        UpdateMissionSettings();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>"Tu versión del juego (0.0.3) es anterior a la que necesitan las misiones (0.0.4 · Prototipo 2)…" o null.</summary>
    private string? MissionVersionWarning(MissionSet set)
    {
        if (set.MinGameVersion is not { } min) return null;
        var info = _installStatus?.State == InstallState.Installed ? _installStatus.Info : null;
        var installed = info?.ParsedVersion;
        if (info == null || installed == null || installed >= min) return null;
        return $"Tu versión del juego ({DisplayOf(info)}) es anterior a la que necesitan las misiones ({min.ToDisplayString()}): " +
               "actualiza el juego para que cuenten.";
    }

    /// <summary>Cada 30 s (reloj de launcher.json): el conjunto puede empezar o terminar.</summary>
    private void OnMissionsClockTick()
    {
        var set = _missionSet;
        if (set == null || _disposed) return;
        bool visible = _settings.ShowMissions && set.IsActive(UtcNow());
        if (visible != Missions.IsVisible) RefreshMissionsCard();
        if (set.HasEnded(UtcNow()) && _missionState is { FinalSent: false })
            RunFlow(() => MaybeSendMissionResultsAsync(force: false), "Misiones: envío final", showErrors: false);
    }

    // ───────────── Logs del juego ─────────────

    /// <summary>Al cerrarse el juego: espera a que Unity termine de escribir y procesa sus eventos.</summary>
    private async Task ProcessMissionLogsAfterGameAsync()
    {
        if (_missionSet == null) return;
        try
        {
            await Task.Delay(MissionLogSettleDelay, _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await ProcessMissionLogsAsync(afterGame: true);
        await MaybeSendMissionResultsAsync(force: false);
    }

    /// <summary>
    /// Lee Player-prev.log y Player.log (en segundo plano), aplica las sesiones nuevas a las misiones y guarda. Con
    /// <paramref name="afterGame"/>, avisa de las misiones completadas. Nunca lanza.
    /// </summary>
    internal async Task ProcessMissionLogsAsync(bool afterGame)
    {
        var set = _missionSet;
        var state = _missionState;
        if (set == null || state == null || _missionLogsRunning || _disposed) return;
        _missionLogsRunning = true;
        try
        {
            var (playerLog, prevLog) = await GetMissionLogPathsAsync();
            if (playerLog == null && prevLog == null) return;

            // Una partida en curso no se procesa (se marcaría como procesada y lo que pase después ya no contaría): con el
            // juego abierto, o si Player.log se escribió hace nada sin session_end (juego abierto fuera del launcher).
            bool gameRunning = IsGameRunning;
            var sessions = await Task.Run(() =>
            {
                var list = new List<GameSession>();
                list.AddRange(GameEventParser.ParseFile(prevLog));
                var current = GameEventParser.ParseFile(playerLog);
                list.AddRange(current);
                if (current.Count > 0 && !current[current.Count - 1].EndedCleanly && (gameRunning || (!afterGame && WasWrittenRecently(playerLog))))
                    list.Remove(current[current.Count - 1]);
                return list;
            });
            if (_disposed || !ReferenceEquals(set, _missionSet) || !ReferenceEquals(state, _missionState)) return;

            var result = MissionEvaluator.Apply(set, state, sessions);
            if (!result.Changed) return;

            _missionRevision++;
            Log.Info($"Misiones: {result.SessionsCounted} sesión(es) nuevas" +
                     (result.SessionsTooOld > 0 ? $" ({result.SessionsTooOld} con una versión anterior a la necesaria)" : "") +
                     (result.NewlyCompleted.Count > 0 ? $"; completadas: {string.Join(", ", result.NewlyCompleted.Select(m => m.Id))}" : "") + ".");
            await SaveMissionStateAsync();
            RefreshMissionsCard();

            if (result.NewlyCompleted.Count > 0 && (afterGame || set.IsActive(UtcNow())))
                ShowToast(MissionsCompletedToast(result.NewlyCompleted));
            else if (afterGame && result.SessionsTooOld > 0 && result.SessionsCounted == 0)
                ShowToast("Tus misiones no cuentan con esta versión del juego: actualízalo.");
        }
        catch (Exception ex)
        {
            Log.Error("Misiones: error al procesar los eventos del juego.", ex);
        }
        finally
        {
            _missionLogsRunning = false;
        }
    }

    /// <summary>¿El archivo cambió en los últimos 2 minutos? (el juego podría seguir escribiéndolo).</summary>
    private static bool WasWrittenRecently(string? path)
    {
        try
        {
            return path != null && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal static string MissionsCompletedToast(IReadOnlyList<Mission> completed)
    {
        var names = string.Join(", ", completed.Take(3).Select(m => m.Title)) + (completed.Count > 3 ? "…" : "");
        return completed.Count == 1 ? $"✓ Misión completada: {names}" : $"✓ {completed.Count} misiones completadas: {names}";
    }

    private async Task<(string? PlayerLog, string? PlayerPrevLog)> GetMissionLogPathsAsync()
    {
        if (MissionLogPathsOverride is { } overridePaths) return await overridePaths();
        var paths = await FindBugLogPathsAsync();
        return (paths.PlayerLog, paths.PlayerPrevLog);
    }

    // ───────────── Marcas del jugador ─────────────

    /// <summary>Funcionó / Falló / No la probé / Deshacer / respuesta a la comprobación, desde la tarjeta.</summary>
    internal void OnMissionAction(string missionId, MissionUserAction action, string? note)
    {
        var set = _missionSet;
        var state = _missionState;
        var mission = set?.Missions.FirstOrDefault(m => string.Equals(m.Id, missionId, StringComparison.Ordinal));
        if (set == null || state == null || mission == null || _disposed) return;

        var progress = state.GetOrCreate(mission);
        note = FeedbackNote(note);
        switch (action)
        {
            case MissionUserAction.Worked:
                if (!mission.Manual) return;
                progress.State = MissionState.Done;
                progress.Automatic = false;
                progress.CompletedUtc = UtcNow();
                progress.GameVersion = _installStatus?.Info?.Version;
                break;
            case MissionUserAction.Failed:
                if (progress.State == MissionState.Done && progress.Automatic) return;
                progress.State = MissionState.Failed;
                progress.Note = note;
                break;
            case MissionUserAction.Skip:
                if (progress.State != MissionState.Pending) return;
                progress.State = MissionState.Skipped;
                break;
            case MissionUserAction.Undo:
                if (progress.State == MissionState.Done && progress.Automatic) return;
                progress.State = MissionState.Pending;
                progress.Note = null;
                progress.CompletedUtc = null;
                break;
            case MissionUserAction.CheckYes:
                if (progress.State != MissionState.Done) return;
                progress.CheckAnswer = true;
                break;
            case MissionUserAction.CheckNo:
                if (progress.State != MissionState.Done) return;
                progress.CheckAnswer = false;
                progress.Note = note ?? progress.Note;
                break;
        }

        Log.Info($"Misiones: «{mission.Id}» → {action} ({progress.State}).");
        _missionRevision++;
        state.Dirty = true;
        RefreshMissionsCard();
        RunFlow(SaveMissionStateAsync, "Misiones: guardar", showErrors: false);
        ScheduleMissionSend(MissionSendDebounce);
    }

    private static string? FeedbackNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var trimmed = note.Trim();
        return trimmed.Length > MissionNotes.MaxNoteLength ? trimmed[..MissionNotes.MaxNoteLength] : trimmed;
    }

    private async Task SaveMissionStateAsync()
    {
        var state = _missionState;
        var store = _missionStore;
        if (state == null || store == null) return;
        string json;
        try
        {
            json = MissionStateStore.Serialize(state); // en el hilo de la UI, que es el único que lo modifica
        }
        catch (Exception ex)
        {
            Log.Warn("Misiones: no se pudo preparar el estado para guardarlo.", ex);
            return;
        }
        var setId = state.SetId;
        await Task.Run(() => store.SaveSerialized(setId, json));
    }

    // ───────────── Envío a Discord ─────────────

    private void ScheduleMissionSend(TimeSpan delay)
    {
        if (_disposed) return;
        if (_missionSendTimer == null)
        {
            _missionSendTimer = new DispatcherTimer(DispatcherPriority.Background);
            _missionSendTimer.Tick += (_, _) =>
            {
                _missionSendTimer.Stop();
                RunFlow(() => MaybeSendMissionResultsAsync(force: false), "Misiones: envío", showErrors: false);
            };
        }
        _missionSendTimer.Stop();
        _missionSendTimer.Interval = delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
        _missionSendTimer.Start();
    }

    /// <summary>En la comprobación periódica: reintenta lo pendiente.</summary>
    private Task PeriodicMissionsAsync() => MaybeSendMissionResultsAsync(force: false);

    /// <summary>«Enviar resultados ahora».</summary>
    private async Task SendMissionResultsNowAsync()
    {
        if (_missionState == null) return;
        _missionSendBlockedUntil = null;
        var status = await MaybeSendMissionResultsAsync(force: true);
        ShowToast(status switch
        {
            MissionSendStatus.Sent => "Resultados de las misiones enviados. ¡Gracias!",
            MissionSendStatus.NoWebhook or MissionSendStatus.WebhookInvalid =>
                "Tus resultados están guardados y se enviarán cuando el equipo active los envíos.",
            null => "No hay nada nuevo que enviar.",
            _ => "No se pudieron enviar ahora; se reintentará solo.",
        });
    }

    /// <summary>
    /// Envía (o actualiza) el mensaje de resultados si hay cambios, respetando el intervalo mínimo salvo con
    /// <paramref name="force"/>. Tras el fin del conjunto envía una vez la versión final. Devuelve el resultado, o null si
    /// no hacía falta enviar. Nunca lanza.
    /// </summary>
    internal async Task<MissionSendStatus?> MaybeSendMissionResultsAsync(bool force)
    {
        var set = _missionSet;
        var state = _missionState;
        var reports = _missionReports;
        if (set == null || state == null || reports == null || _missionSendRunning || _disposed) return null;

        var now = UtcNow();
        bool final = set.HasEnded(now) && !state.FinalSent;
        bool hasAnything = state.SessionsCounted > 0 || state.Missions.Values.Any(m => m.State != MissionState.Pending);
        if (!hasAnything) return null;                           // nada que contar aún
        if (!state.Dirty && !final && !force) return null;
        if (!force && _missionSendBlockedUntil is { } blocked && now < blocked) return null;
        if (!force && !final && state.LastSentUtc is { } last && now - last < MissionSendThrottle)
        {
            ScheduleMissionSend(MissionSendThrottle - (now - last));
            return null;
        }

        _missionSendRunning = true;
        try
        {
            var webhook = set.DiscordWebhookUrl ?? (_survey ?? await GetSurveyAsync(refresh: false)).DiscordWebhookUrl;
            var key = MissionReportService.WebhookKey(webhook);
            var messageId = key != null && string.Equals(key, state.DiscordWebhookKey, StringComparison.Ordinal) ? state.DiscordMessageId : null;

            var info = _installStatus?.Info;
            var context = new MissionReportContext
            {
                Nickname = _settings.FeedbackNickname,
                LauncherVersion = AppInfo.VersionText,
                GameVersionDisplay = info != null ? DisplayOf(info) : null,
                Final = final,
                NowUtc = now,
            };
            var payload = MissionReportService.BuildPayload(set, state, context);
            var revision = _missionRevision;

            var result = await reports.SendAsync(payload, webhook, messageId, _lifetimeCts.Token);
            if (_disposed || !ReferenceEquals(state, _missionState)) return result.Status;
            _lastMissionSendStatus = result.Status;

            switch (result.Status)
            {
                case MissionSendStatus.Sent:
                    state.DiscordMessageId = result.MessageId;
                    state.DiscordWebhookKey = key;
                    state.LastSentUtc = now;
                    if (final) state.FinalSent = true;
                    if (revision == _missionRevision) state.Dirty = false;
                    break;
                case MissionSendStatus.RateLimited:
                    _missionSendBlockedUntil = now + (result.RetryAfter ?? TimeSpan.FromMinutes(1));
                    break;
                case MissionSendStatus.Rejected:
                    // Reintentar el mismo mensaje no serviría: se espera al siguiente cambio.
                    if (revision == _missionRevision) state.Dirty = false;
                    if (final) state.FinalSent = true;
                    break;
                case MissionSendStatus.WebhookInvalid:
                    state.DiscordMessageId = null;
                    break;
            }
            await SaveMissionStateAsync();
            return result.Status;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Misiones: error inesperado al enviar los resultados.", ex);
            return MissionSendStatus.Failed;
        }
        finally
        {
            _missionSendRunning = false;
            UpdateMissionSettings();
        }
    }

    /// <summary>Ajustes: «Resultados de las misiones pendientes de enviar».</summary>
    private void UpdateMissionSettings()
    {
        var state = _missionState;
        bool pending = state is { Dirty: true } && (state.SessionsCounted > 0 || state.Missions.Values.Any(m => m.State != MissionState.Pending));
        bool waitsForTeam = _lastMissionSendStatus is MissionSendStatus.NoWebhook or MissionSendStatus.WebhookInvalid;
        Settings.SetPendingMissions(pending, waitsForTeam, _missionSet != null);
    }

    // ───────────── Argumentos del juego ─────────────

    /// <summary>
    /// Añade <c>-palitoSession &lt;id&gt;</c> (docs/EVENTOS-JUEGO.md, sección 6): el juego lo copia en session_start y así se
    /// sabe que ese log es de esta ejecución.
    /// </summary>
    internal static string AppendMissionSession(string arguments, out string sessionId)
    {
        sessionId = Guid.NewGuid().ToString("N");
        var trimmed = (arguments ?? "").Trim();
        return (trimmed.Length > 0 ? trimmed + " " : "") + "-palitoSession " + sessionId;
    }

    /// <summary>
    /// Instancias extra del modo desarrollador: su propio log (argumento estándar de Unity) en
    /// &lt;AppData&gt;\extra-instance-logs para no mezclar eventos con el del juego principal. Se borran los de hace más de 3 días.
    /// </summary>
    /// <summary>Carpeta de los logs de las instancias extra (no la de los logs del launcher).</summary>
    internal static string ExtraInstanceLogsDir => Path.Combine(AppPaths.AppDataDir, "extra-instance-logs");

    internal static string AppendExtraInstanceLog(string arguments)
    {
        // Si el jugador ya eligió su propio log en los argumentos, se respeta.
        if ((arguments ?? "").Contains("-logFile", StringComparison.OrdinalIgnoreCase)) return arguments ?? "";
        string path;
        try
        {
            var dir = ExtraInstanceLogsDir;
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.EnumerateFiles(dir, "game-extra-*.log"))
            {
                if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-3)) FileSystemUtil.TryDeleteFile(old);
            }
            path = Path.Combine(dir,
                $"game-extra-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..4]}.log");
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo preparar el log de la instancia extra; usará el de Unity.", ex);
            return arguments;
        }
        var trimmed = (arguments ?? "").Trim();
        return (trimmed.Length > 0 ? trimmed + " " : "") + "-logFile \"" + path + "\"";
    }

    // ───────────── Diagnóstico ─────────────

    private string MissionsDiagnosticText()
    {
        var set = _missionSet;
        var state = _missionState;
        if (_missionsProvider == null) return "desactivadas (no se pudieron preparar)";
        if (set == null || state == null) return "no hay conjunto activo";
        var counts = set.Missions
            .Select(m => state.Missions.TryGetValue(m.Id, out var p) ? p.State : MissionState.Pending)
            .GroupBy(s => s)
            .Select(g => $"{g.Key}: {g.Count()}");
        return $"«{set.Id}» ({string.Join(", ", counts)}; {state.SessionsCounted} sesiones, {state.SessionsTooOld} antiguas; " +
               $"{(state.Dirty ? "pendiente de enviar" : "enviado")}" +
               (state.LastSentUtc is { } sent ? $" el {sent.ToString("u", CultureInfo.InvariantCulture)}" : "") + ")";
    }
}
