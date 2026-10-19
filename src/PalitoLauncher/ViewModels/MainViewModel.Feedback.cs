using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Helpers;

namespace PalitoLauncher.ViewModels;

// Opiniones: invitación tras la partida, encuesta («Enviar opinión») y bandeja de salida de respuestas pendientes.
public sealed partial class MainViewModel
{
    /// <summary>Lo que se espera a la encuesta remota al abrir el diálogo antes de usar la de por defecto.</summary>
    private static readonly TimeSpan SurveyLoadTimeout = TimeSpan.FromSeconds(4);

    internal const string FeedbackSentToast = "¡Gracias! Tu opinión ya le ha llegado al equipo.";

    /// <summary>En la bandeja con webhook: falló la red o Discord (se reintenta solo).</summary>
    internal const string FeedbackQueuedToast = "¡Gracias! Se enviará en cuanto haya conexión.";

    /// <summary>En la bandeja porque la encuesta no tiene webhook (aún no se publicó o se borró): no es culpa de la conexión.</summary>
    internal const string FeedbackQueuedNoWebhookToast =
        "¡Gracias! Tu opinión se ha guardado y se enviará en cuanto el equipo active los envíos.";

    private const string LauncherUpdatingSurveyToast = "Espera a que termine la actualización del launcher para dar tu opinión.";

    /// <summary>Versión del juego de una partida (se toma al lanzarlo o engancharse: luego puede actualizarse o desinstalarse).</summary>
    internal readonly record struct PlayedGameVersion(string? Version, string? Display);

    private SurveyDefinition? _survey;
    private Task<SurveyDefinition>? _surveyLoadTask;
    private DateTimeOffset? _gameSessionStartUtc;
    private PlayedGameVersion? _sessionGameVersion;
    private bool _surveyOpen;
    private SurveyDialogViewModel? _openSurvey;
    private bool _isWindowMinimized;
    private (int Minutes, int? ExitCode, PlayedGameVersion? Version)? _pendingFeedbackPrompt;
    private int _promptMinutes;
    private int? _promptExitCode;
    private PlayedGameVersion? _promptGameVersion;
    private int? _crashSessionMinutes;
    private int? _crashExitCode;
    private PlayedGameVersion? _crashGameVersion;
    private bool _flushRunning;
    private int _pendingFeedbackCount;

    private bool _isFeedbackPromptVisible;
    private string _feedbackPromptTitle = "";
    private int _feedbackPromptRating;

    // ───────────── Propiedades para las vistas ─────────────

    /// <summary>Tarjeta "¿Qué tal la partida?" (abajo a la derecha, no modal).</summary>
    public bool IsFeedbackPromptVisible { get => _isFeedbackPromptVisible; private set => SetProperty(ref _isFeedbackPromptVisible, value); }

    /// <summary>"¿Qué tal la partida de 34 min?".</summary>
    public string FeedbackPromptTitle { get => _feedbackPromptTitle; private set => SetProperty(ref _feedbackPromptTitle, value); }

    public string FeedbackPromptText => $"Tu opinión ayuda a mejorar {Game}.";

    /// <summary>Estrellas de la invitación (0 = ninguna). Se ponen a 0 cada vez que se muestra.</summary>
    public int FeedbackPromptRating { get => _feedbackPromptRating; set => SetProperty(ref _feedbackPromptRating, Math.Clamp(value, 0, 5)); }

    /// <summary>Abre la encuesta (Trigger = Manual). Siempre disponible, aunque el juego no esté instalado.</summary>
    public ICommand SendFeedbackCommand { get; }

    /// <summary>Desde la invitación: parámetro = estrellas elegidas (1..5) o nada («Dar mi opinión»).</summary>
    public ICommand OpenFeedbackFromPromptCommand { get; }

    /// <summary>«Ahora no».</summary>
    public ICommand DismissFeedbackPromptCommand { get; }

    /// <summary>«No volver a preguntar» (AskFeedbackAfterSession = false).</summary>
    public ICommand NeverAskFeedbackCommand { get; }

    /// <summary>Ajustes › «Reintentar envío» de las opiniones pendientes.</summary>
    public ICommand RetryFeedbackSendCommand { get; }

    // ───────────── Servicios ─────────────

    /// <summary>Nunca impide abrir el launcher: si no se puede crear, las opiniones quedan desactivadas (y se registra).</summary>
    private static SurveyProvider? CreateSurveyProvider(GitHubClient github)
    {
        try
        {
            return new SurveyProvider(github, AppPaths.AppDataDir);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar la encuesta de opinión.", ex);
            return null;
        }
    }

    private static FeedbackService? CreateFeedbackService()
    {
        try
        {
            return new FeedbackService(HttpFactory.Shared, AppPaths.FeedbackOutboxDir);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar el envío de opiniones.", ex);
            return null;
        }
    }

    /// <summary>Al arrancar (en segundo plano): encuesta y vaciado de la bandeja de salida, sin molestar.</summary>
    private async Task InitializeFeedbackAsync()
    {
        await GetSurveyAsync(refresh: true);
        await FlushFeedbackOutboxAsync(refreshSurvey: false);
    }

    /// <summary>
    /// Encuesta efectiva (local → remota → por defecto). Con <paramref name="refresh"/> se vuelve a pedir (no gasta cupo de
    /// la API: raw.githubusercontent.com con caché). Una sola carga a la vez. Solo lanza OperationCanceledException al cerrar.
    /// </summary>
    private Task<SurveyDefinition> GetSurveyAsync(bool refresh)
    {
        if (!refresh && _survey != null) return Task.FromResult(_survey);
        var running = _surveyLoadTask;
        if (running != null) return running;

        var task = LoadSurveyCoreAsync();
        if (!task.IsCompleted) _surveyLoadTask = task;
        return task;
    }

    private async Task<SurveyDefinition> LoadSurveyCoreAsync()
    {
        try
        {
            SurveyDefinition? survey = null;
            if (_surveyProvider != null)
            {
                try
                {
                    survey = await _surveyProvider.GetSurveyAsync(_lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Error("Error inesperado al obtener la encuesta.", ex);
                }
            }

            survey ??= DefaultSurvey();
            _survey = survey;
            return survey;
        }
        finally
        {
            _surveyLoadTask = null;
        }
    }

    /// <summary>Encuesta para el diálogo: la ya cargada o, si tarda más de unos segundos, la de por defecto.</summary>
    private async Task<SurveyDefinition> GetSurveyForDialogAsync()
    {
        if (_survey != null) return _survey;
        var load = GetSurveyAsync(refresh: false);
        var finished = await Task.WhenAny(load, Task.Delay(SurveyLoadTimeout, _lifetimeCts.Token));
        if (finished == load) return await load;
        Log.Warn("La encuesta tarda en llegar; se usa la de por defecto.");
        return DefaultSurvey();
    }

    /// <summary>Encuesta por defecto del Core; si fallara, una mínima local (la encuesta nunca debe romper nada).</summary>
    private static SurveyDefinition DefaultSurvey()
    {
        try
        {
            return SurveyProvider.CreateDefault();
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear la encuesta por defecto; se usa una mínima.", ex);
            return new SurveyDefinition
            {
                Id = "default",
                Title = $"Tu opinión sobre {Game}",
                Questions = new List<SurveyQuestion>
                {
                    new() { Id = "overall", Type = SurveyQuestionType.Rating, Text = "En general, ¿qué tal la partida?", Required = true },
                    new() { Id = "improve", Type = SurveyQuestionType.Text, Text = "¿Qué cambiarías o mejorarías?" },
                },
            };
        }
    }

    // ───────────── Encuesta (diálogo) ─────────────

    /// <summary>
    /// Se puede abrir la encuesta: no hay otra abierta ni se está actualizando el launcher (al terminar se reinicia solo y
    /// se perdería lo escrito).
    /// </summary>
    private bool CanOpenSurvey => !_surveyOpen && _operationKind != OperationKind.LauncherUpdate;

    /// <summary>
    /// La encuesta está abierta con respuestas sin enviar: cerrar el launcher las perdería. No notifica cambios (lo consulta
    /// la ventana al cerrar).
    /// </summary>
    public bool HasUnsentFeedback => _openSurvey is { HasUnsentAnswers: true };

    /// <summary>
    /// Antes de cerrar el launcher: si la encuesta tiene respuestas sin enviar, ella misma pregunta «Tienes una opinión sin
    /// enviar. ¿Salir sin enviarla?» [Salir] [Volver]. True = se puede cerrar (la encuesta se descartó o no había nada que
    /// perder); false = el usuario volvió a la encuesta.
    /// </summary>
    public Task<bool> ConfirmCloseWithUnsentFeedbackAsync()
    {
        var survey = _openSurvey;
        return survey == null ? Task.FromResult(true) : survey.ConfirmExitAsync();
    }

    /// <summary>Cierra la encuesta abierta si está vacía (no hay nada que perder). Para que no tape otra pregunta.</summary>
    private void CloseEmptySurvey()
    {
        if (_openSurvey?.CloseIfEmpty() == true) Log.Info("Encuesta vacía cerrada para poder preguntar por el cierre.");
    }

    /// <summary>
    /// Abre la encuesta. Al enviarla muestra el toast de gracias. Una sola a la vez. Tras una partida se adjunta la versión
    /// que se jugó (<paramref name="played"/>); en «Enviar opinión», la instalada ahora.
    /// </summary>
    private async Task OpenSurveyAsync(FeedbackTrigger trigger, int? sessionMinutes, int? exitCode, int? initialRating,
        PlayedGameVersion? played = null)
    {
        if (_surveyOpen || _disposed) return;
        if (_operationKind == OperationKind.LauncherUpdate)
        {
            ShowToast(LauncherUpdatingSurveyToast);
            return;
        }

        _surveyOpen = true;
        HideFeedbackPrompt();
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var survey = await GetSurveyForDialogAsync();
            // Mientras llegaba la encuesta (hasta 4 s, aún sin overlay) pudo empezar la actualización del launcher.
            if (_operationKind == OperationKind.LauncherUpdate || _disposed)
            {
                if (!_disposed) ShowToast(LauncherUpdatingSurveyToast);
                return;
            }
            var version = (trigger == FeedbackTrigger.AfterSession ? played : null) ?? CurrentGameVersion();
            var context = new SurveyContext
            {
                Trigger = trigger,
                SessionMinutes = sessionMinutes,
                ExitCode = exitCode,
                InitialRating = initialRating,
                Nickname = _settings.FeedbackNickname,
                GameVersion = version?.Version,
                GameVersionDisplay = version?.Display,
                LauncherVersion = AppInfo.VersionText,
                OsDescription = OsDescriptionSafe(),
            };

            var dialog = new SurveyDialogViewModel(survey, context, submission => SubmitFeedbackAsync(submission, survey));
            _openSurvey = dialog;
            Log.Info($"Encuesta «{survey.Id}» abierta ({trigger}).");
            var result = await Dialogs.ShowDialogAsync(dialog, dialog.Completion);
            if (result != null) ShowToast(FeedbackResultToast(result.Outcome, survey));
        }
        finally
        {
            _openSurvey = null;
            _surveyOpen = false;
            CommandManager.InvalidateRequerySuggested();
        }
        await RefreshPendingFeedbackAsync();
    }

    /// <summary>Toast tras enviar: llegó, o quedó en la bandeja (por la conexión o porque aún no hay webhook).</summary>
    internal static string FeedbackResultToast(FeedbackSendOutcome outcome, SurveyDefinition? survey)
    {
        if (outcome == FeedbackSendOutcome.Sent) return FeedbackSentToast;
        return HasValidWebhook(survey) ? FeedbackQueuedToast : FeedbackQueuedNoWebhookToast;
    }

    /// <summary>
    /// Lo llama el diálogo al pulsar Enviar: recuerda el apodo (si cambió) y envía (o deja en la bandeja). Los errores
    /// esperables llegan al diálogo como LauncherException y se muestran allí sin cerrar.
    /// </summary>
    private async Task<FeedbackSendOutcome> SubmitFeedbackAsync(FeedbackSubmission submission, SurveyDefinition survey)
    {
        var nickname = string.IsNullOrWhiteSpace(submission.Nickname) ? null : submission.Nickname.Trim();
        if (!string.Equals(nickname ?? "", _settings.FeedbackNickname ?? "", StringComparison.Ordinal))
        {
            _settings.FeedbackNickname = nickname;
            _ = SaveSettingsAsync();
            Settings.ReloadFeedbackSettings();
        }

        if (_feedback == null)
            throw new LauncherException("No se pudo preparar el envío de opiniones. Cierra y vuelve a abrir el launcher e inténtalo de nuevo.");

        var outcome = await _feedback.SubmitAsync(submission, survey.DiscordWebhookUrl, _lifetimeCts.Token);
        Log.Info($"Opinión {submission.Id} ({submission.Trigger}, encuesta «{submission.SurveyId}»): {outcome}.");
        return outcome;
    }

    /// <summary>Versión instalada ahora mismo (null si no hay juego instalado).</summary>
    private PlayedGameVersion? CurrentGameVersion()
    {
        var info = _installStatus?.Info;
        return info == null ? null : new PlayedGameVersion(GameVersionOf(info), DisplayOf(info));
    }

    private static string? GameVersionOf(InstalledGameInfo? info)
    {
        if (info == null) return null;
        try
        {
            return !string.IsNullOrWhiteSpace(info.Version) ? info.Version : info.ParsedVersion?.ToString();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la versión instalada para la opinión.", ex);
            return info.Version;
        }
    }

    private static string? OsDescriptionSafe()
    {
        try
        {
            return RuntimeInformation.OSDescription;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la versión de Windows.", ex);
            return null;
        }
    }

    // ───────────── Partida e invitación ─────────────

    /// <summary>
    /// El launcher lanzó el juego o se enganchó a uno abierto: empieza a contar la partida y anota qué versión se juega
    /// (la opinión debe llevar esa, aunque al responder ya se haya actualizado).
    /// </summary>
    private void MarkGameSessionStarted(bool restart)
    {
        if (restart || _gameSessionStartUtc == null)
        {
            _gameSessionStartUtc = DateTimeOffset.UtcNow;
            _sessionGameVersion = CurrentGameVersion();
        }
        HideFeedbackPrompt(); // se oculta sola si empieza otra partida
    }

    /// <summary>Duración de la partida que acaba de terminar (y deja de contar).</summary>
    private TimeSpan TakeGameSessionDuration()
    {
        var start = _gameSessionStartUtc;
        _gameSessionStartUtc = null;
        if (start == null) return TimeSpan.Zero;
        var duration = DateTimeOffset.UtcNow - start.Value;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>Versión que se jugó en la partida que acaba de terminar (si no se supo al empezar, la instalada ahora).</summary>
    private PlayedGameVersion? TakeSessionGameVersion()
    {
        var played = _sessionGameVersion ?? CurrentGameVersion();
        _sessionGameVersion = null;
        return played;
    }

    /// <summary>"34 min" / "1 h 05 min".</summary>
    internal static string FormatSessionLength(int minutes)
    {
        minutes = Math.Max(1, minutes);
        if (minutes < 60) return $"{minutes.ToString(CultureInfo.InvariantCulture)} min";
        return $"{(minutes / 60).ToString(CultureInfo.InvariantCulture)} h {(minutes % 60).ToString("00", CultureInfo.InvariantCulture)} min";
    }

    private static int SessionMinutesOf(TimeSpan duration) => Math.Max(1, (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero));

    /// <summary>Código de salida para la opinión: -1 (desconocido) → null.</summary>
    private static int? FeedbackExitCode(int exitCode) => exitCode == -1 ? null : exitCode;

    /// <summary>
    /// Tras una partida (sin aviso de cierre inesperado): si toca según la política (ajuste del usuario, encuesta, duración
    /// e intervalo entre invitaciones), muestra la invitación; si la ventana está minimizada, espera a que se restaure.
    /// <paramref name="played"/>: versión que se jugó (null = la instalada ahora).
    /// </summary>
    internal async Task MaybePromptFeedbackAsync(TimeSpan duration, int exitCode, PlayedGameVersion? played = null)
    {
        played ??= CurrentGameVersion();
        if (_disposed) return;
        var survey = await GetSurveyForDialogAsync();

        bool should;
        try
        {
            should = FeedbackService.ShouldPromptAfterSession(_settings.AskFeedbackAfterSession, survey, duration,
                _settings.LastFeedbackPromptUtc, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Error("Error al decidir si pedir opinión tras la partida.", ex);
            return;
        }

        if (!should)
        {
            Log.Debug($"Sin invitación a opinar tras la partida ({duration.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min).");
            return;
        }
        if (_disposed || IsGameRunning || _surveyOpen) return; // ya empezó otra partida o ya está respondiendo

        int minutes = SessionMinutesOf(duration);
        var code = FeedbackExitCode(exitCode);
        if (_isWindowMinimized)
        {
            _pendingFeedbackPrompt = (minutes, code, played);
            Log.Info("Invitación a opinar en espera hasta que se restaure la ventana.");
            return;
        }
        ShowFeedbackPrompt(minutes, code, played);
    }

    /// <summary>
    /// Muestra la invitación ya (y anota la hora para no volver a preguntar demasiado pronto). <paramref name="played"/>:
    /// versión que se jugó (null = la instalada ahora).
    /// </summary>
    internal void ShowFeedbackPrompt(int sessionMinutes, int? exitCode, PlayedGameVersion? played = null)
    {
        if (_disposed) return;
        _pendingFeedbackPrompt = null;
        _promptMinutes = Math.Max(1, sessionMinutes);
        _promptExitCode = exitCode;
        _promptGameVersion = played ?? CurrentGameVersion();
        FeedbackPromptTitle = $"¿Qué tal la partida de {FormatSessionLength(_promptMinutes)}?";
        FeedbackPromptRating = 0;
        IsFeedbackPromptVisible = true;
        _settings.LastFeedbackPromptUtc = DateTimeOffset.UtcNow;
        _ = SaveSettingsAsync();
        Log.Info($"Invitación a opinar mostrada (partida de {_promptMinutes} min).");
    }

    private void HideFeedbackPrompt()
    {
        _pendingFeedbackPrompt = null;
        IsFeedbackPromptVisible = false;
    }

    /// <summary>La ventana se minimizó o se restauró (la invitación espera a que se vea).</summary>
    public void SetWindowMinimized(bool minimized)
    {
        _isWindowMinimized = minimized;
        if (minimized || _pendingFeedbackPrompt is not { } pending) return;
        if (IsGameRunning || _surveyOpen)
        {
            _pendingFeedbackPrompt = null;
            return;
        }
        ShowFeedbackPrompt(pending.Minutes, pending.ExitCode, pending.Version);
    }

    private void OpenFeedbackFromPrompt(object? parameter)
    {
        int? rating = parameter switch
        {
            int i when i is >= 1 and <= 5 => i,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 5 => n,
            _ => null,
        };
        int minutes = _promptMinutes;
        var code = _promptExitCode;
        var played = _promptGameVersion;
        RunFlow(() => OpenSurveyAsync(FeedbackTrigger.AfterSession, minutes, code, rating, played), "Encuesta tras la partida");
    }

    private void DismissFeedbackPrompt()
    {
        HideFeedbackPrompt();
        Log.Info("Invitación a opinar: «Ahora no».");
    }

    private void NeverAskFeedback()
    {
        HideFeedbackPrompt();
        _settings.AskFeedbackAfterSession = false;
        _ = SaveSettingsAsync();
        Settings.ReloadFeedbackSettings();
        Log.Info("El usuario pidió no volver a recibir invitaciones a opinar.");
        ShowToast("No volveremos a preguntarte. Puedes cambiarlo en Ajustes › Juego.");
    }

    /// <summary>«Contarnos qué pasó» del aviso de cierre inesperado.</summary>
    private Task TellWhatHappenedAsync()
        => OpenSurveyAsync(FeedbackTrigger.AfterSession, _crashSessionMinutes, _crashExitCode, null, _crashGameVersion);

    // ───────────── Bandeja de salida ─────────────

    /// <summary>Reintenta lo pendiente de la bandeja (sin molestar). Devuelve cuántas se enviaron. Nunca lanza salvo al cerrar.</summary>
    private async Task<int> FlushFeedbackOutboxAsync(bool refreshSurvey)
    {
        if (_feedback == null || _flushRunning || _disposed) return 0;
        _flushRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var survey = await GetSurveyAsync(refreshSurvey);
            int sent = await _feedback.FlushOutboxAsync(survey.DiscordWebhookUrl, _lifetimeCts.Token);
            if (sent > 0) Log.Info($"Bandeja de opiniones: {sent} enviada(s).");
            return sent;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al vaciar la bandeja de opiniones.", ex);
            return 0;
        }
        finally
        {
            _flushRunning = false;
            CommandManager.InvalidateRequerySuggested();
            await RefreshPendingFeedbackAsync();
        }
    }

    /// <summary>En la comprobación periódica: refresca la encuesta y, si hay pendientes, reintenta enviarlas.</summary>
    private async Task PeriodicFeedbackAsync()
    {
        if (_pendingFeedbackCount > 0) await FlushFeedbackOutboxAsync(refreshSurvey: true);
        else await GetSurveyAsync(refresh: true);
    }

    private async Task<int> PendingFeedbackCountSafeAsync()
    {
        var feedback = _feedback;
        if (feedback == null) return 0;
        try
        {
            return await Task.Run(() => feedback.PendingCount);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo contar las opiniones pendientes.", ex);
            return 0;
        }
    }

    /// <summary>
    /// Actualiza "N opiniones pendientes de enviar" en Ajustes (sin webhook en la encuesta: "…se enviarán cuando el equipo
    /// active los envíos"). Nunca lanza.
    /// </summary>
    private async Task RefreshPendingFeedbackAsync()
    {
        int count = await PendingFeedbackCountSafeAsync();
        if (_disposed) return;
        _pendingFeedbackCount = count;
        Settings.SetPendingFeedback(count, waitsForTeam: _survey != null && !HasValidWebhook(_survey));
    }

    private Task RetryFeedbackSendAsync() => Guarded(async () =>
    {
        int before = await PendingFeedbackCountSafeAsync();
        if (before == 0)
        {
            await RefreshPendingFeedbackAsync();
            ShowToast("No hay opiniones pendientes de enviar.");
            return;
        }

        int sent = await FlushFeedbackOutboxAsync(refreshSurvey: true);
        ShowToast(BuildRetryFeedbackToast(before, sent, _pendingFeedbackCount, HasValidWebhook(_survey)));
    }, "Reintentar envío de opiniones");

    /// <summary>
    /// Resultado de «Reintentar envío» tal como fue: enviadas, apartadas (Discord las rechazó o estaban dañadas: van a
    /// failed\ y no se reintentan) y las que siguen pendientes.
    /// </summary>
    internal static string BuildRetryFeedbackToast(int before, int sent, int after, bool hasWebhook)
    {
        sent = Math.Max(0, sent);
        after = Math.Max(0, after);
        int setAside = Math.Max(0, before - sent - after);

        if (setAside == 0 && after == 0)
        {
            if (sent == 0) return "No hay opiniones pendientes de enviar.";
            return sent == 1 ? "Listo: se envió la opinión pendiente." : "Listo: se enviaron todas las opiniones pendientes.";
        }

        if (sent == 0 && setAside == 0)
        {
            return hasWebhook
                ? "No se pudieron enviar ahora. Se reintentará automáticamente más tarde."
                : "Todavía no se pueden enviar. Se enviarán solas en cuanto el equipo active los envíos.";
        }

        var parts = new List<string>();
        if (sent > 0) parts.Add(sent == 1 ? "Se envió 1 opinión." : $"Se enviaron {sent} opiniones.");
        if (setAside > 0)
        {
            parts.Add(setAside == 1
                ? "1 no se pudo enviar y se ha apartado (no se volverá a intentar)."
                : $"{setAside} no se pudieron enviar y se han apartado (no se volverá a intentar).");
        }
        if (after > 0)
            parts.Add(after == 1 ? "1 sigue pendiente y se reintentará más tarde." : $"{after} siguen pendientes y se reintentarán más tarde.");
        return string.Join(" ", parts);
    }

    private static bool HasValidWebhook(SurveyDefinition? survey)
    {
        try
        {
            return SurveyProvider.IsValidDiscordWebhook(survey?.DiscordWebhookUrl);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar el webhook de las opiniones.", ex);
            return false;
        }
    }
}
