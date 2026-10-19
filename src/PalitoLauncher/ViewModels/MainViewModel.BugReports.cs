using System.IO;
using System.Windows.Input;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Net;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Helpers;

namespace PalitoLauncher.ViewModels;

// «Reportar un problema»: diálogo, envío al canal #bugs de Discord (webhook de launcher.json o, si no hay, el de la
// encuesta) con los registros anonimizados, y bandeja de salida de los reportes que aún no se pudieron enviar.
public sealed partial class MainViewModel
{
    internal const string BugReportSentToast = "¡Gracias! Tu reporte ya le ha llegado al equipo.";

    /// <summary>En la bandeja con webhook: falló la red o Discord (se reintenta solo).</summary>
    internal const string BugReportQueuedToast = "¡Gracias! Tu reporte se ha guardado y se enviará en cuanto haya conexión.";

    /// <summary>En la bandeja porque no hay webhook (ni en launcher.json ni en la encuesta): no es culpa de la conexión.</summary>
    internal const string BugReportQueuedNoWebhookToast =
        "¡Gracias! Tu reporte se ha guardado y se enviará en cuanto el equipo active los envíos.";

    private const string LauncherUpdatingReportToast = "Espera a que termine la actualización del launcher para reportar un problema.";

    /// <summary>Lo que se copia como mucho de cada log (lo mismo que BugReportService: su final).</summary>
    private const long BugLogTailBytes = 2 * 1024 * 1024;

    /// <summary>Al arrancar, cuánto se espera como mucho a la primera lectura de launcher.json antes de vaciar la bandeja.</summary>
    private static readonly TimeSpan BugOutboxStartupWait = TimeSpan.FromSeconds(20);

    private readonly BugReportService? _bugReports;

    /// <summary>Se completa tras el primer intento de leer launcher.json (bien o mal): ya se sabe qué webhook usar.</summary>
    private readonly TaskCompletionSource _manifestFirstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _bugReportOpen;
    private BugReportDialogViewModel? _openBugReport;
    private bool _bugFlushRunning;
    private int _pendingBugReportCount;

    /// <summary>De dónde se abre el reporte y con qué datos (categoría, aviso, partida…).</summary>
    internal sealed record BugReportRequest(
        BugCategory Category,
        string Origin,
        string? Intro = null,
        int? ExitCode = null,
        PlayedGameVersion? Version = null,
        string? ShownError = null);

    /// <summary>Rutas de los registros que se adjuntarían (cualquiera puede ser null o no existir).</summary>
    internal sealed record BugLogPaths(string? PlayerLog, string? PlayerPrevLog, string? LauncherLog);

    // ───────────── Comandos ─────────────

    /// <summary>Menú ⋯ y Ajustes › «Reportar un problema». Siempre disponible (salvo con otro reporte abierto).</summary>
    public ICommand ReportProblemCommand { get; }

    /// <summary>Ajustes › «Reintentar envío» de los reportes pendientes.</summary>
    public ICommand RetryBugReportSendCommand { get; }

    /// <summary>Se puede abrir el diálogo: no hay otro abierto ni se está actualizando el launcher (se perdería lo escrito).</summary>
    private bool CanOpenBugReport => !_bugReportOpen && _operationKind != OperationKind.LauncherUpdate;

    // ───────────── Servicio ─────────────

    /// <summary>Nunca impide abrir el launcher: si no se puede crear, los reportes quedan desactivados (y se registra).</summary>
    private static BugReportService? CreateBugReportService()
    {
        try
        {
            return new BugReportService(HttpFactory.Shared, AppPaths.BugReportOutboxDir);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo preparar el envío de reportes de problemas.", ex);
            return null;
        }
    }

    /// <summary>
    /// Al arrancar (en segundo plano): vacía la bandeja de reportes cuando ya se sabe qué webhook usar (tras la primera
    /// lectura de launcher.json, como mucho unos segundos).
    /// </summary>
    private async Task InitializeBugReportsAsync()
    {
        if (_bugReports == null) return;
        await Task.WhenAny(_manifestFirstAttempt.Task, Task.Delay(BugOutboxStartupWait, _lifetimeCts.Token));
        if (_disposed) return;
        await FlushBugReportOutboxAsync();
    }

    /// <summary>En la comprobación periódica: si hay reportes pendientes, se reintenta enviarlos.</summary>
    private async Task PeriodicBugReportsAsync()
    {
        if (_pendingBugReportCount > 0) await FlushBugReportOutboxAsync();
    }

    // ───────────── Webhook ─────────────

    /// <summary>Webhook de los reportes ya conocido: bugReportWebhookUrl de launcher.json si es válido; si no, el de la encuesta.</summary>
    private string? CurrentBugReportWebhook()
    {
        var fromManifest = _manifest.BugReportWebhookUrl;
        return IsValidWebhookSafe(fromManifest) ? fromManifest : _survey?.DiscordWebhookUrl;
    }

    private bool HasValidBugReportWebhook() => IsValidWebhookSafe(CurrentBugReportWebhook());

    /// <summary>
    /// Webhook para enviar: el de launcher.json o, si no hay, el de la encuesta. Con <paramref name="waitForSurvey"/> se
    /// espera a que llegue la encuesta (bandeja en segundo plano); si no, como mucho unos segundos (el usuario espera).
    /// </summary>
    private async Task<string?> GetBugReportWebhookAsync(bool waitForSurvey)
    {
        var fromManifest = _manifest.BugReportWebhookUrl;
        if (IsValidWebhookSafe(fromManifest)) return fromManifest;
        var survey = _survey ?? (waitForSurvey ? await GetSurveyAsync(refresh: false) : await GetSurveyForDialogAsync());
        return survey.DiscordWebhookUrl;
    }

    private static bool IsValidWebhookSafe(string? url)
    {
        try
        {
            return SurveyProvider.IsValidDiscordWebhook(url);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo comprobar el webhook de los reportes.", ex);
            return false;
        }
    }

    // ───────────── Diálogo ─────────────

    /// <summary>«Reportar un problema» desde el menú ⋯ o Ajustes.</summary>
    private Task ReportProblemAsync() => OpenBugReportAsync(new BugReportRequest(BugCategory.Bug, "manual"));

    /// <summary>«Reportar el cierre» del aviso de cierre inesperado: categoría Crash con el código y la versión de esa partida.</summary>
    private async Task ReportCrashAsync()
    {
        var exitCode = _crashExitCode;
        var played = _crashGameVersion;
        var codeText = exitCode is { } code ? $" (código {code})" : "";
        bool sent = await OpenBugReportAsync(new BugReportRequest(BugCategory.Crash, "aviso de cierre inesperado",
            Intro: $"{Game} se cerró de forma inesperada{codeText}. Cuéntanos qué estabas haciendo justo antes: con los registros de la partida es mucho más fácil encontrar el fallo.",
            ExitCode: exitCode, Version: played));

        // Ya está contado: el aviso deja de verse (si sigue siendo el de esa partida).
        if (sent && _crashNotice != null && _crashExitCode == exitCode)
        {
            _crashNotice = null;
            UpdateState();
        }
    }

    /// <summary>«Reportar el problema» de un diálogo de error: categoría Launcher, con el error en el resumen.</summary>
    private Task ReportErrorAsync(string title, string message)
        => OpenBugReportAsync(new BugReportRequest(BugCategory.Launcher, "diálogo de error",
            Intro: "Cuéntanos qué estabas haciendo cuando apareció el error. El mensaje del error se adjunta en el resumen.",
            ShownError: $"{title}: {message}"));

    /// <summary>
    /// Abre el diálogo (uno a la vez). Los tamaños de los registros se miran en segundo plano antes de mostrarlo. Al
    /// enviarlo, toast veraz (enviado / se enviará con conexión / cuando el equipo active los envíos). True si se envió o
    /// quedó guardado en la bandeja; false si se canceló o no se abrió.
    /// </summary>
    private async Task<bool> OpenBugReportAsync(BugReportRequest request)
    {
        if (_bugReportOpen || _disposed) return false;
        if (_operationKind == OperationKind.LauncherUpdate)
        {
            ShowToast(LauncherUpdatingReportToast);
            return false;
        }

        _bugReportOpen = true;
        CommandManager.InvalidateRequerySuggested();
        bool submitted = false;
        try
        {
            var version = request.Version ?? CurrentGameVersion();
            var paths = await FindBugLogPathsAsync();
            var previews = await Task.Run(() => BuildAttachmentPreviews(paths));

            // Mientras se miraban los registros pudo empezar la actualización del launcher (o cerrarse).
            if (_operationKind == OperationKind.LauncherUpdate || _disposed)
            {
                if (!_disposed) ShowToast(LauncherUpdatingReportToast);
                return false;
            }

            var context = new BugReportContext
            {
                Category = request.Category,
                Intro = request.Intro,
                ExitCode = request.ExitCode,
                Nickname = _settings.FeedbackNickname,
                GameVersion = version?.Version,
                GameVersionDisplay = version?.Display,
                LauncherVersion = AppInfo.VersionText,
                OsDescription = OsDescriptionSafe(),
                Attachments = previews,
            };

            var dialog = new BugReportDialogViewModel(context,
                (report, attachLogs) => SubmitBugReportAsync(report, attachLogs ? paths : null, request));
            _openBugReport = dialog;
            Log.Info($"Reportar un problema: diálogo abierto ({request.Origin}).");
            var result = await Dialogs.ShowDialogAsync(dialog, dialog.Completion);
            if (result != null)
            {
                submitted = true;
                ShowToast(BugReportResultToast(result.Outcome, HasValidBugReportWebhook()));
            }
        }
        finally
        {
            _openBugReport = null;
            _bugReportOpen = false;
            CommandManager.InvalidateRequerySuggested();
        }

        await RefreshPendingBugReportsAsync();
        return submitted;
    }

    /// <summary>Toast tras enviar: llegó, o quedó en la bandeja (por la conexión o porque aún no hay webhook).</summary>
    internal static string BugReportResultToast(FeedbackSendOutcome outcome, bool hasWebhook)
    {
        if (outcome == FeedbackSendOutcome.Sent) return BugReportSentToast;
        return hasWebhook ? BugReportQueuedToast : BugReportQueuedNoWebhookToast;
    }

    /// <summary>
    /// Lo llama el diálogo al pulsar Enviar: recuerda el nombre (el mismo que el de las opiniones) y envía con
    /// BugReportService (o lo deja en la bandeja). Los errores esperables llegan al diálogo como LauncherException.
    /// <paramref name="logs"/> = null si el usuario desmarcó «Adjuntar registros» (entonces no se adjunta nada).
    /// </summary>
    private async Task<FeedbackSendOutcome> SubmitBugReportAsync(BugReport report, BugLogPaths? logs, BugReportRequest request)
    {
        RememberFeedbackNickname(report.Nickname);

        if (_bugReports == null)
            throw new LauncherException("No se pudo preparar el envío de reportes. Cierra y vuelve a abrir el launcher e inténtalo de nuevo.");

        BugReportSources? sources = null;
        if (logs != null)
        {
            var summary = BuildDiagnosticSummary();
            summary["Origen del reporte"] = request.Origin;
            if (!string.IsNullOrWhiteSpace(request.ShownError)) summary["Error mostrado"] = request.ShownError;
            sources = new BugReportSources
            {
                PlayerLog = logs.PlayerLog,
                PlayerPrevLog = logs.PlayerPrevLog,
                LauncherLog = logs.LauncherLog,
                Summary = summary,
            };
        }

        var webhook = await GetBugReportWebhookAsync(waitForSurvey: false);
        var outcome = await _bugReports.SubmitAsync(report, sources, webhook, _lifetimeCts.Token);
        Log.Info($"Reporte {report.Id} ({report.Category}, {request.Origin}, registros: {(sources != null ? "sí" : "no")}): {outcome}.");
        return outcome;
    }

    /// <summary>Player.log / Player-prev.log (carpeta de datos de Unity del juego instalado) y el log de hoy del launcher.</summary>
    private async Task<BugLogPaths> FindBugLogPathsAsync()
    {
        string? unityDir = null;
        var root = _installRoot;
        if (root != null)
        {
            try
            {
                unityDir = await Task.Run(() => _installer.GetUnityPersistentDataDir(root));
            }
            catch (Exception ex)
            {
                Log.Warn("No se pudo localizar la carpeta de datos del juego para el reporte.", ex);
            }
        }

        string? launcherLog = null;
        try
        {
            launcherLog = Log.CurrentLogFile;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo localizar el log del launcher para el reporte.", ex);
        }

        return new BugLogPaths(
            unityDir == null ? null : Path.Combine(unityDir, "Player.log"),
            unityDir == null ? null : Path.Combine(unityDir, "Player-prev.log"),
            launcherLog);
    }

    /// <summary>Lista «Se adjuntará:» con el tamaño real de cada registro (hilo de fondo: mira el disco). Nunca lanza.</summary>
    internal static IReadOnlyList<BugReportAttachmentPreview> BuildAttachmentPreviews(BugLogPaths paths) => new[]
    {
        Preview("Player.log", "Registro del juego (última partida)", paths.PlayerLog),
        Preview("Player-prev.log", "Registro de la partida anterior", paths.PlayerPrevLog),
        Preview("launcher.log", "Registro del launcher (hoy)", paths.LauncherLog),
        new BugReportAttachmentPreview("summary.txt", "Resumen: versiones, ajustes y estado del launcher", "pocos KB", true),
    };

    private static BugReportAttachmentPreview Preview(string name, string description, string? path)
    {
        long? size = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var info = new FileInfo(path);
                if (info.Exists) size = info.Length;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo mirar el tamaño de {name}: {ex.Message}");
        }

        if (size is not { } bytes) return new BugReportAttachmentPreview(name, description, "no encontrado", false);
        var sizeText = bytes > BugLogTailBytes
            ? $"últimos {UiFormat.Bytes(BugLogTailBytes)} de {UiFormat.Bytes(bytes)}"
            : UiFormat.Bytes(bytes);
        return new BugReportAttachmentPreview(name, description, sizeText, true);
    }

    /// <summary>Cierra el reporte abierto si no tiene nada escrito (para que no tape otra pregunta al cerrar).</summary>
    private void CloseEmptyBugReport()
    {
        if (_openBugReport?.CloseIfEmpty() == true) Log.Info("Reporte vacío cerrado para poder preguntar por el cierre.");
    }

    // ───────────── Bandeja de salida ─────────────

    /// <summary>Reintenta lo pendiente (sin molestar). Devuelve cuántos se enviaron. Nunca lanza salvo al cerrar.</summary>
    private async Task<int> FlushBugReportOutboxAsync()
    {
        if (_bugReports == null || _bugFlushRunning || _disposed) return 0;
        _bugFlushRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var webhook = await GetBugReportWebhookAsync(waitForSurvey: true);
            int sent = await _bugReports.FlushOutboxAsync(webhook, _lifetimeCts.Token);
            if (sent > 0) Log.Info($"Bandeja de reportes: {sent} enviado(s).");
            return sent;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al vaciar la bandeja de reportes.", ex);
            return 0;
        }
        finally
        {
            _bugFlushRunning = false;
            CommandManager.InvalidateRequerySuggested();
            await RefreshPendingBugReportsAsync();
        }
    }

    private async Task<int> PendingBugReportCountSafeAsync()
    {
        var service = _bugReports;
        if (service == null) return 0;
        try
        {
            return await Task.Run(() => service.PendingCount);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo contar los reportes pendientes.", ex);
            return 0;
        }
    }

    /// <summary>Actualiza «N reportes pendientes de enviar» en Ajustes. Nunca lanza.</summary>
    private async Task RefreshPendingBugReportsAsync()
    {
        int count = await PendingBugReportCountSafeAsync();
        if (_disposed) return;
        _pendingBugReportCount = count;
        Settings.SetPendingBugReports(count, waitsForTeam: _survey != null && !HasValidBugReportWebhook());
    }

    private Task RetryBugReportSendAsync() => Guarded(async () =>
    {
        int before = await PendingBugReportCountSafeAsync();
        if (before == 0)
        {
            await RefreshPendingBugReportsAsync();
            ShowToast("No hay reportes pendientes de enviar.");
            return;
        }

        int sent = await FlushBugReportOutboxAsync();
        ShowToast(BuildRetryBugReportToast(before, sent, _pendingBugReportCount, HasValidBugReportWebhook()));
    }, "Reintentar envío de reportes");

    /// <summary>Resultado de «Reintentar envío» de los reportes: enviados, apartados (Discord los rechazó) y pendientes.</summary>
    internal static string BuildRetryBugReportToast(int before, int sent, int after, bool hasWebhook)
    {
        sent = Math.Max(0, sent);
        after = Math.Max(0, after);
        int setAside = Math.Max(0, before - sent - after);

        if (setAside == 0 && after == 0)
        {
            if (sent == 0) return "No hay reportes pendientes de enviar.";
            return sent == 1 ? "Listo: se envió el reporte pendiente." : "Listo: se enviaron todos los reportes pendientes.";
        }

        if (sent == 0 && setAside == 0)
        {
            return hasWebhook
                ? "No se pudieron enviar ahora. Se reintentará automáticamente más tarde."
                : "Todavía no se pueden enviar. Se enviarán solos en cuanto el equipo active los envíos.";
        }

        var parts = new List<string>();
        if (sent > 0) parts.Add(sent == 1 ? "Se envió 1 reporte." : $"Se enviaron {sent} reportes.");
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
}
