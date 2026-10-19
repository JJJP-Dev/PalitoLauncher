using System.IO;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.State;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Opiniones y roadmap dentro del MainViewModel (sin InitializeAsync, así que nunca consulta GitHub): invitación tras la
/// partida (política, ventana minimizada, «No volver a preguntar», salida provocada por el launcher, versión jugada),
/// apertura de la encuesta desde la invitación y desde «Enviar opinión», envío real a la bandeja de salida sin webhook
/// (sin red), textos veraces (toast, Ajustes, «Reintentar envío»), cierre de la ventana con la encuesta a medias y
/// presentación del roadmap.
/// </summary>
public sealed class MainViewModelFeedbackTests
{
    private static Task WithViewModel(Func<MainViewModel, Task> test) => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        MainViewModelAccess.Survey(vm) = TestFeedback.Survey(); // encuesta ya "cargada": no se pide la remota
        await test(vm);
    });

    private static async Task<SurveyDialogViewModel> WaitForSurveyAsync(MainViewModel vm)
    {
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog is SurveyDialogViewModel, "diálogo de la encuesta");
        return (SurveyDialogViewModel)vm.Dialogs.CurrentDialog!;
    }

    private static async Task CloseSurveyAsync(MainViewModel vm, SurveyDialogViewModel dialog)
    {
        dialog.Cancel();
        if (dialog.IsConfirmingDiscard) dialog.DiscardCommand.Execute(null);
        Assert.Null(await dialog.Completion);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");
    }

    // ───────────── Invitación tras la partida ─────────────

    [Fact]
    public Task Prompt_ShowsTheSessionLength_AndRecordsWhenItWasShown() => WithViewModel(vm =>
    {
        Assert.False(vm.IsFeedbackPromptVisible);
        var before = DateTimeOffset.UtcNow;

        vm.ShowFeedbackPrompt(34, 0);

        Assert.True(vm.IsFeedbackPromptVisible);
        Assert.Equal("¿Qué tal la partida de 34 min?", vm.FeedbackPromptTitle);
        Assert.Equal("Tu opinión ayuda a mejorar Coop2D.", vm.FeedbackPromptText);
        Assert.NotNull(vm.CurrentSettings.LastFeedbackPromptUtc);
        Assert.InRange(vm.CurrentSettings.LastFeedbackPromptUtc!.Value, before, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(1, "1 min")]
    [InlineData(34, "34 min")]
    [InlineData(59, "59 min")]
    [InlineData(60, "1 h 00 min")]
    [InlineData(65, "1 h 05 min")]
    [InlineData(0, "1 min")]
    public void SessionLength_IsFormattedForPeople(int minutes, string expected)
        => Assert.Equal(expected, MainViewModel.FormatSessionLength(minutes));

    [Fact]
    public Task Prompt_NotNow_Hides() => WithViewModel(vm =>
    {
        vm.ShowFeedbackPrompt(20, 0);
        vm.DismissFeedbackPromptCommand.Execute(null);
        Assert.False(vm.IsFeedbackPromptVisible);
        Assert.True(vm.CurrentSettings.AskFeedbackAfterSession);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Prompt_NeverAskAgain_TurnsTheSettingOff() => WithViewModel(vm =>
    {
        vm.ShowFeedbackPrompt(20, 0);
        var changed = new List<string?>();
        vm.Settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.NeverAskFeedbackCommand.Execute(null);

        Assert.False(vm.IsFeedbackPromptVisible);
        Assert.False(vm.CurrentSettings.AskFeedbackAfterSession);
        Assert.False(vm.Settings.AskFeedbackAfterSession);
        Assert.Contains(nameof(SettingsViewModel.AskFeedbackAfterSession), changed); // el interruptor de Ajustes se entera
        Assert.Contains("Ajustes", vm.ToastText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AfterSession_FollowsThePolicy() => WithViewModel(async vm =>
    {
        // Partida corta (encuesta: mínimo 5 min) → nada.
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(2), 0);
        Assert.False(vm.IsFeedbackPromptVisible);

        // Partida larga → invitación.
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(30), 0);
        Assert.True(vm.IsFeedbackPromptVisible);
        Assert.Equal("¿Qué tal la partida de 30 min?", vm.FeedbackPromptTitle);

        // Otra al poco rato → no se repite (mínimo 12 h entre invitaciones).
        vm.DismissFeedbackPromptCommand.Execute(null);
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(45), 0);
        Assert.False(vm.IsFeedbackPromptVisible);

        // Pasado el intervalo vuelve a preguntar, salvo que el usuario lo haya desactivado.
        vm.CurrentSettings.LastFeedbackPromptUtc = DateTimeOffset.UtcNow.AddHours(-13);
        vm.CurrentSettings.AskFeedbackAfterSession = false;
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(45), 0);
        Assert.False(vm.IsFeedbackPromptVisible);
        vm.CurrentSettings.AskFeedbackAfterSession = true;
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(45), 0);
        Assert.True(vm.IsFeedbackPromptVisible);
    });

    [Fact]
    public Task AfterSession_RemoteSurveyCanDisableThePrompt() => WithViewModel(async vm =>
    {
        MainViewModelAccess.Survey(vm)!.AskAfterSession = false;
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(60), 0);
        Assert.False(vm.IsFeedbackPromptVisible);
    });

    [Fact]
    public Task AfterSession_WhileMinimized_WaitsForTheWindowToBeRestored() => WithViewModel(async vm =>
    {
        vm.SetWindowMinimized(true);
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(30), 0);
        Assert.False(vm.IsFeedbackPromptVisible);
        Assert.Null(vm.CurrentSettings.LastFeedbackPromptUtc); // aún no se ha mostrado

        vm.SetWindowMinimized(false);
        Assert.True(vm.IsFeedbackPromptVisible);
        Assert.NotNull(vm.CurrentSettings.LastFeedbackPromptUtc);

        // Restaurar otra vez no la vuelve a "mostrar" si ya se cerró.
        vm.DismissFeedbackPromptCommand.Execute(null);
        vm.SetWindowMinimized(true);
        vm.SetWindowMinimized(false);
        Assert.False(vm.IsFeedbackPromptVisible);
    });

    [Fact]
    public Task PromptStars_OpenTheSurveyWithThatRating_AndTheSessionData() => WithViewModel(async vm =>
    {
        await vm.MaybePromptFeedbackAsync(TimeSpan.FromMinutes(25), -1); // -1 = código desconocido
        Assert.True(vm.IsFeedbackPromptVisible);

        vm.OpenFeedbackFromPromptCommand.Execute(4);
        var dialog = await WaitForSurveyAsync(vm);

        Assert.False(vm.IsFeedbackPromptVisible);
        Assert.Equal(FeedbackTrigger.AfterSession, dialog.Trigger);
        Assert.Equal(4, dialog.Questions.OfType<RatingQuestionViewModel>().First().Value);
        var submission = dialog.BuildSubmission();
        Assert.Equal(25, submission.SessionMinutes);
        Assert.Null(submission.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(submission.LauncherVersion));
        Assert.False(string.IsNullOrWhiteSpace(submission.OsDescription));
        Assert.Null(submission.GameVersion); // sin juego instalado

        // Mientras está abierta no se abre otra.
        Assert.False(vm.SendFeedbackCommand.CanExecute(null));
        await CloseSurveyAsync(vm, dialog);
        await WpfHost.WaitUntilAsync(() => vm.SendFeedbackCommand.CanExecute(null), "«Enviar opinión» disponible otra vez");
    });

    [Fact]
    public Task PromptButton_WithoutStars_OpensTheSurveyUnrated() => WithViewModel(async vm =>
    {
        vm.ShowFeedbackPrompt(12, 0);
        vm.OpenFeedbackFromPromptCommand.Execute(null);
        var dialog = await WaitForSurveyAsync(vm);
        Assert.Equal(0, dialog.Questions.OfType<RatingQuestionViewModel>().First().Value);
        Assert.Equal(0, dialog.BuildSubmission().ExitCode);
        await CloseSurveyAsync(vm, dialog);
    });

    [Fact]
    public Task CrashNotice_OffersToTellWhatHappened() => WithViewModel(async vm =>
    {
        MainViewModelAccess.CrashNotice(vm) = "Coop2D se cerró de forma inesperada (código 3).";
        MainViewModelAccess.UpdateState(vm);
        Assert.Equal("Reportar el cierre", vm.SecondaryNoticeActionText); // 1.0.5: antes «Crear informe» (sigue en el menú ⋯)
        Assert.Equal("Contarnos qué pasó", vm.SecondaryNoticeAction2Text);
        Assert.True(vm.HasSecondaryNoticeAction2);

        vm.SecondaryNoticeAction2Command.Execute(null);
        var dialog = await WaitForSurveyAsync(vm);
        Assert.Equal(FeedbackTrigger.AfterSession, dialog.Trigger);
        await CloseSurveyAsync(vm, dialog);

        vm.DismissSecondaryNoticeCommand.Execute(null);
        Assert.False(vm.HasSecondaryNoticeAction2);
    });

    // ───────────── «Enviar opinión» ─────────────

    [Fact]
    public Task ManualFeedback_IsAlwaysAvailable_AndQueuesWithoutAWebhook() => WithViewModel(async vm =>
    {
        try
        {
            Assert.True(vm.SendFeedbackCommand.CanExecute(null)); // aunque el juego no esté instalado
            vm.SendFeedbackCommand.Execute(null);
            var dialog = await WaitForSurveyAsync(vm);
            Assert.Equal(FeedbackTrigger.Manual, dialog.Trigger);
            Assert.Equal("~1 minuto", dialog.MetaText); // sin versión del juego

            dialog.Questions.OfType<RatingQuestionViewModel>().First().Value = 5;
            dialog.Questions.OfType<ChoiceQuestionViewModel>().First().Options[0].IsSelected = true;
            dialog.Nickname = " Tester ";

            // Sin webhook en la encuesta: se guarda en la bandeja de salida sin tocar la red.
            await ((AsyncRelayCommand)dialog.SendCommand).ExecuteAsync(null);
            Assert.False(dialog.HasError, dialog.ErrorMessage);
            Assert.Equal(FeedbackSendOutcome.Queued, (await dialog.Completion)!.Outcome);
            await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");

            // Sin webhook no es cosa de la conexión: el toast y Ajustes lo dicen así.
            Assert.Equal(MainViewModel.FeedbackQueuedNoWebhookToast, vm.ToastText);
            Assert.Equal("Tester", vm.CurrentSettings.FeedbackNickname); // se recuerda el apodo
            Assert.Equal("Tester", vm.Settings.FeedbackNickname);
            await WpfHost.WaitUntilAsync(() => vm.Settings.HasPendingFeedback, "opiniones pendientes en Ajustes");
            Assert.True(vm.Settings.PendingFeedbackWaitsForTeam);
            Assert.Equal("1 opinión pendiente: se enviará cuando el equipo active los envíos", vm.Settings.PendingFeedbackText);
        }
        finally
        {
            try
            {
                if (Directory.Exists(AppPaths.FeedbackOutboxDir)) Directory.Delete(AppPaths.FeedbackOutboxDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    });

    [Fact]
    public Task Settings_PendingFeedbackText() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        Assert.False(vm.Settings.HasPendingFeedback);
        vm.Settings.SetPendingFeedback(3);
        Assert.True(vm.Settings.HasPendingFeedback);
        Assert.False(vm.Settings.PendingFeedbackWaitsForTeam);
        Assert.Equal("3 opiniones pendientes de enviar", vm.Settings.PendingFeedbackText);
        vm.Settings.SetPendingFeedback(1);
        Assert.Equal("1 opinión pendiente de enviar", vm.Settings.PendingFeedbackText);

        // Sin webhook en la encuesta esperan al equipo, no a la conexión.
        var changed = new List<string?>();
        vm.Settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Settings.SetPendingFeedback(1, waitsForTeam: true);
        Assert.Contains(nameof(SettingsViewModel.PendingFeedbackText), changed);
        Assert.True(vm.Settings.PendingFeedbackWaitsForTeam);
        Assert.Equal("1 opinión pendiente: se enviará cuando el equipo active los envíos", vm.Settings.PendingFeedbackText);
        vm.Settings.SetPendingFeedback(4, waitsForTeam: true);
        Assert.Equal("4 opiniones pendientes: se enviarán cuando el equipo active los envíos", vm.Settings.PendingFeedbackText);
        return Task.CompletedTask;
    });

    [Fact]
    public void FeedbackToast_TellsTheRealReason()
    {
        const string webhook = "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDEFGHIJ-0123456789";
        Assert.True(SurveyProvider.IsValidDiscordWebhook(webhook)); // si no, la prueba no miraría el caso "con webhook"

        Assert.Equal(MainViewModel.FeedbackSentToast, MainViewModel.FeedbackResultToast(FeedbackSendOutcome.Sent, TestFeedback.Survey(webhook)));
        Assert.Equal(MainViewModel.FeedbackQueuedToast, MainViewModel.FeedbackResultToast(FeedbackSendOutcome.Queued, TestFeedback.Survey(webhook)));
        Assert.Equal("¡Gracias! Se enviará en cuanto haya conexión.", MainViewModel.FeedbackQueuedToast);

        // Sin webhook (o con uno que no es de Discord) se guarda hasta que el equipo active los envíos.
        Assert.Equal(MainViewModel.FeedbackQueuedNoWebhookToast, MainViewModel.FeedbackResultToast(FeedbackSendOutcome.Queued, TestFeedback.Survey()));
        Assert.Equal(MainViewModel.FeedbackQueuedNoWebhookToast,
            MainViewModel.FeedbackResultToast(FeedbackSendOutcome.Queued, TestFeedback.Survey("https://example.com/hook")));
        Assert.Equal(MainViewModel.FeedbackQueuedNoWebhookToast, MainViewModel.FeedbackResultToast(FeedbackSendOutcome.Queued, null));
        Assert.Equal("¡Gracias! Tu opinión se ha guardado y se enviará en cuanto el equipo active los envíos.",
            MainViewModel.FeedbackQueuedNoWebhookToast);
    }

    [Theory]
    [InlineData(1, 1, 0, true, "Listo: se envió la opinión pendiente.")]
    [InlineData(3, 3, 0, true, "Listo: se enviaron todas las opiniones pendientes.")]
    [InlineData(2, 0, 2, true, "No se pudieron enviar ahora. Se reintentará automáticamente más tarde.")]
    [InlineData(2, 0, 2, false, "Todavía no se pueden enviar. Se enviarán solas en cuanto el equipo active los envíos.")]
    [InlineData(3, 1, 2, true, "Se envió 1 opinión. 2 siguen pendientes y se reintentarán más tarde.")]
    // Discord las rechazó (o estaban dañadas): se apartan y no se reintentan; nunca "todas".
    [InlineData(1, 0, 0, true, "1 no se pudo enviar y se ha apartado (no se volverá a intentar).")]
    [InlineData(2, 1, 0, true, "Se envió 1 opinión. 1 no se pudo enviar y se ha apartado (no se volverá a intentar).")]
    [InlineData(5, 2, 1, true, "Se enviaron 2 opiniones. 2 no se pudieron enviar y se han apartado (no se volverá a intentar). 1 sigue pendiente y se reintentará más tarde.")]
    public void RetryFeedbackToast_SaysWhatHappened(int before, int sent, int after, bool hasWebhook, string expected)
        => Assert.Equal(expected, MainViewModel.BuildRetryFeedbackToast(before, sent, after, hasWebhook));

    // ───────────── Cerrar el launcher con la encuesta abierta ─────────────

    [Fact]
    public Task Closing_WithUnsentAnswers_AsksFirst() => WithViewModel(async vm =>
    {
        Assert.Equal(CloseDecision.Allow, vm.GetCloseDecision());
        vm.SendFeedbackCommand.Execute(null);
        var dialog = await WaitForSurveyAsync(vm);

        // Abierta pero vacía: se puede cerrar sin preguntar.
        Assert.False(vm.HasUnsentFeedback);
        Assert.Equal(CloseDecision.Allow, vm.GetCloseDecision());

        dialog.Questions.OfType<TextQuestionViewModel>().First().AnswerText = "Me encantó el modo cooperativo";
        Assert.True(vm.HasUnsentFeedback);
        Assert.Equal(CloseDecision.ConfirmUnsentFeedback, vm.GetCloseDecision());

        // «Volver»: no se cierra y lo escrito sigue ahí.
        var leave = vm.ConfirmCloseWithUnsentFeedbackAsync();
        Assert.True(dialog.IsConfirmingExit);
        Assert.Equal("Tienes una opinión sin enviar. ¿Salir sin enviarla?", dialog.ConfirmText);
        dialog.KeepEditingCommand.Execute(null);
        Assert.False(await leave);
        Assert.Same(dialog, vm.Dialogs.CurrentDialog);
        Assert.Equal(CloseDecision.ConfirmUnsentFeedback, vm.GetCloseDecision());

        // «Salir»: la encuesta se descarta y ya se puede cerrar.
        leave = vm.ConfirmCloseWithUnsentFeedbackAsync();
        dialog.DiscardCommand.Execute(null);
        Assert.True(await leave);
        Assert.False(vm.HasUnsentFeedback);
        Assert.Equal(CloseDecision.Allow, vm.GetCloseDecision());
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");
        Assert.Null(await dialog.Completion);
    });

    [Fact]
    public Task Closing_WithoutASurvey_NeverAsks() => WithViewModel(async vm =>
    {
        Assert.False(vm.HasUnsentFeedback);
        Assert.True(await vm.ConfirmCloseWithUnsentFeedbackAsync());
    });

    [Fact]
    public Task Survey_CannotBeOpenedWhileTheLauncherUpdates() => WithViewModel(async vm =>
    {
        // Al terminar, la versión nueva arranca y esta se cierra al momento: se perdería lo escrito.
        MainViewModelAccess.OperationKind(vm) = OperationKind.LauncherUpdate;
        try
        {
            Assert.False(vm.SendFeedbackCommand.CanExecute(null));
            vm.ShowFeedbackPrompt(20, 0);
            Assert.False(vm.OpenFeedbackFromPromptCommand.CanExecute(4));

            // «Contarnos qué pasó» no pasa por esos comandos: tampoco abre la encuesta.
            MainViewModelAccess.CrashNotice(vm) = "Coop2D se cerró de forma inesperada (código 3).";
            MainViewModelAccess.UpdateState(vm);
            vm.SecondaryNoticeAction2Command.Execute(null);
            await WpfHost.FlushAsync();
            Assert.Null(vm.Dialogs.CurrentDialog);
            Assert.Contains("actualización del launcher", vm.ToastText);
        }
        finally
        {
            MainViewModelAccess.OperationKind(vm) = OperationKind.None;
        }
        Assert.True(vm.SendFeedbackCommand.CanExecute(null));
    });

    // ───────────── Fin de la partida ─────────────

    [Fact]
    public Task GameExit_Invites_WithTheVersionThatWasPlayed() => WithViewModel(async vm =>
    {
        // Se jugó la 0.0.3 («Jugar sin actualizar»)...
        MainViewModelAccess.StartSession(vm, "0.0.3-prototype.2", "0.0.3 · Prototipo 2", minutes: 30);
        await MainViewModelAccess.HandleGameExitedAsync(vm, 0);
        await WpfHost.WaitUntilAsync(() => vm.IsFeedbackPromptVisible, "invitación tras la partida");
        Assert.Equal("¿Qué tal la partida de 30 min?", vm.FeedbackPromptTitle);

        // ...y antes de responder se actualizó a la 0.0.4: la opinión es de la 0.0.3.
        MainViewModelAccess.InstallStatus(vm) = MainViewModelAccess.Installed("0.0.4-prototype.3", "0.0.4 · Prototipo 3");
        vm.OpenFeedbackFromPromptCommand.Execute(5);
        var dialog = await WaitForSurveyAsync(vm);
        Assert.Equal("Versión 0.0.3 · Prototipo 2 · ~1 minuto", dialog.MetaText);
        var submission = dialog.BuildSubmission();
        Assert.Equal("0.0.3-prototype.2", submission.GameVersion);
        Assert.Equal("0.0.3 · Prototipo 2", submission.GameVersionDisplay);
        Assert.Equal(30, submission.SessionMinutes);
        Assert.Equal(0, submission.ExitCode);
        await CloseSurveyAsync(vm, dialog);

        // «Enviar opinión» (manual) usa la instalada ahora.
        vm.SendFeedbackCommand.Execute(null);
        dialog = await WaitForSurveyAsync(vm);
        Assert.Equal("0.0.4-prototype.3", dialog.BuildSubmission().GameVersion);
        await CloseSurveyAsync(vm, dialog);
    });

    [Fact]
    public Task GameExit_VersionIsTakenWhenTheSessionStarts() => WithViewModel(async vm =>
    {
        MainViewModelAccess.StartSession(vm, "0.0.3-prototype.2", "0.0.3 · Prototipo 2", minutes: 20);
        MainViewModelAccess.InstallStatus(vm) = MainViewModelAccess.Installed("0.0.9", "0.0.9");
        await MainViewModelAccess.HandleGameExitedAsync(vm, -1);
        await WpfHost.WaitUntilAsync(() => vm.IsFeedbackPromptVisible, "invitación tras la partida");

        vm.OpenFeedbackFromPromptCommand.Execute(null);
        var dialog = await WaitForSurveyAsync(vm);
        Assert.Equal("0.0.3-prototype.2", dialog.BuildSubmission().GameVersion);
        await CloseSurveyAsync(vm, dialog);
    });

    [Fact]
    public Task GameCrash_TellWhatHappened_UsesTheVersionThatWasPlayed() => WithViewModel(async vm =>
    {
        MainViewModelAccess.StartSession(vm, "0.0.3-prototype.2", "0.0.3 · Prototipo 2", minutes: 12);
        await MainViewModelAccess.HandleGameExitedAsync(vm, 3);
        Assert.Equal("Contarnos qué pasó", vm.SecondaryNoticeAction2Text);
        Assert.False(vm.IsFeedbackPromptVisible); // el aviso de cierre sustituye a la invitación

        MainViewModelAccess.InstallStatus(vm) = null; // p.ej. lo desinstaló antes de contarlo
        vm.SecondaryNoticeAction2Command.Execute(null);
        var dialog = await WaitForSurveyAsync(vm);
        var submission = dialog.BuildSubmission();
        Assert.Equal("0.0.3-prototype.2", submission.GameVersion);
        Assert.Equal(12, submission.SessionMinutes);
        Assert.Equal(3, submission.ExitCode);
        await CloseSurveyAsync(vm, dialog);
    });

    [Theory]
    [InlineData(-1)] // Process.Kill
    [InlineData(1)]
    public Task GameExit_ClosedByTheLauncher_NeitherInvitesNorWarns(int exitCode) => WithViewModel(async vm =>
    {
        // «Forzar cierre y continuar» (instalar/actualizar/reparar/desinstalar/mover) o «Forzar cierre».
        MainViewModelAccess.StartSession(vm, "0.0.3-prototype.2", "0.0.3 · Prototipo 2", minutes: 45);
        MainViewModelAccess.SuppressNextCrashNotice(vm) = true;

        await MainViewModelAccess.HandleGameExitedAsync(vm, exitCode);
        await WpfHost.FlushAsync();
        await Task.Delay(50);
        await WpfHost.FlushAsync();

        Assert.False(vm.IsFeedbackPromptVisible);
        Assert.Null(vm.CurrentSettings.LastFeedbackPromptUtc); // no gasta el intervalo de 12 h
        Assert.Null(MainViewModelAccess.CrashNotice(vm));
        Assert.False(MainViewModelAccess.SuppressNextCrashNotice(vm)); // la marca se consume

        // La siguiente partida normal sí invita.
        MainViewModelAccess.StartSession(vm, "0.0.3-prototype.2", "0.0.3 · Prototipo 2", minutes: 45);
        await MainViewModelAccess.HandleGameExitedAsync(vm, 0);
        await WpfHost.WaitUntilAsync(() => vm.IsFeedbackPromptVisible, "invitación tras una partida normal");
    });

    [Fact]
    public Task Settings_NicknameIsSavedOnCommit_EmptyMeansAnonymous() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.Settings.FeedbackNickname = "  Juanjo ";
        Assert.Null(vm.CurrentSettings.FeedbackNickname); // aún no (se guarda al perder el foco)
        vm.Settings.CommitFeedbackNickname();
        Assert.Equal("Juanjo", vm.CurrentSettings.FeedbackNickname);
        vm.Settings.FeedbackNickname = "   ";
        vm.Settings.CommitFeedbackNickname();
        Assert.Null(vm.CurrentSettings.FeedbackNickname);
        return Task.CompletedTask;
    });

    // ───────────── Roadmap ─────────────

    [Fact]
    public Task Roadmap_BeforeLoading_ShowsTheLoadingState() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        Assert.True(vm.IsRoadmapLoading);
        Assert.False(vm.IsRoadmapFailed);
        Assert.False(vm.HasRoadmapItems);
        Assert.False(vm.HasRoadmapCurrent);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Roadmap_GroupsByPhase_SummarizesAndPicksCurrent() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.ApplyRoadmapResult(TestFeedback.Result());

        Assert.False(vm.IsRoadmapLoading);
        Assert.False(vm.IsRoadmapEmpty);
        Assert.True(vm.HasRoadmapItems);
        Assert.Equal("0 de 5 hitos completados", vm.RoadmapSummaryText);
        Assert.Equal(0, vm.RoadmapDoneValue);
        Assert.StartsWith("Comprobado a las ", vm.RoadmapStatusText);
        Assert.False(vm.IsRoadmapStale);

        Assert.Equal(new[] { "Pre-producción", "Producción", "Lanzamiento" }, vm.RoadmapPhases.Select(p => p.Title));
        Assert.Equal("0 de 3 completados", vm.RoadmapPhases[0].CountText);
        Assert.Equal(new[] { false, true, true }, vm.RoadmapPhases[0].Items.Select(i => i.ShowSeparator));
        Assert.Equal(5, vm.RoadmapItems.Count);

        // En Inicio: los que están en curso (Pre 1 y Pre 2).
        Assert.True(vm.HasRoadmapCurrent);
        Assert.Equal(new[] { 1, 2 }, vm.RoadmapCurrent.Select(i => i.Number));
        Assert.Same(vm.RoadmapItems[0], vm.RoadmapCurrent[0]);

        var pre1 = vm.RoadmapItems[0];
        Assert.True(pre1.IsInProgress);
        Assert.True(pre1.ShowProgress);
        Assert.Equal("26 de 27 tareas · 96 %", pre1.ProgressText);
        Assert.InRange(pre1.ProgressValue, 96, 96.5);
        Assert.Equal("En curso", pre1.StatusText);
        Assert.Equal("¿pueden 2-4 jugadores conectarse por internet y moverse?", pre1.Summary, ignoreCase: true);

        var pre3 = vm.RoadmapItems[2];
        Assert.True(pre3.IsNext);
        Assert.False(pre3.ShowProgress);
        Assert.Equal("Siguiente", pre3.StatusText);
        Assert.True(vm.RoadmapItems[3].IsPlanned);
        Assert.False(vm.RoadmapItems[4].HasSummary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Roadmap_DoneMilestones_CountAndShowFullBar() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.ApplyRoadmapResult(TestFeedback.Result(new[]
        {
            TestFeedback.Milestone(1, "Pre 1 · Tech prototype", open: 0, closed: 27, isClosed: true),
            TestFeedback.Milestone(2, "Pre 2 · Gameplay prototype", open: 0, closed: 0),
        }));

        Assert.Equal("1 de 2 hitos completados", vm.RoadmapSummaryText);
        Assert.Equal(50, vm.RoadmapDoneValue);
        var done = vm.RoadmapItems[0];
        Assert.True(done.IsDone);
        Assert.Equal(100, done.ProgressValue);
        Assert.Equal("Completado", done.ProgressText);
        Assert.Equal(new[] { 2 }, vm.RoadmapCurrent.Select(i => i.Number)); // sin ninguno en curso: el siguiente
        Assert.Equal("1 de 2 completados", vm.RoadmapPhases[0].CountText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Roadmap_FromOfflineCache_SaysWhy() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.ApplyRoadmapResult(TestFeedback.Result(source: ReleaseFetchSource.OfflineCache));
        Assert.True(vm.IsRoadmapStale);
        Assert.Equal("Sin conexión con GitHub. Mostrando datos guardados.", vm.RoadmapStatusText);
        Assert.True(vm.HasRoadmapItems);

        // El motivo real (p.ej. el límite de consultas), no "Sin conexión" para todo.
        const string limited = "Límite de consultas a GitHub alcanzado; se restablece a las 14:32. Mostrando datos guardados.";
        vm.ApplyRoadmapResult(new MilestoneFetchResult
        {
            Milestones = TestFeedback.Milestones(), Source = ReleaseFetchSource.OfflineCache, FetchedAt = DateTimeOffset.UtcNow.AddHours(-3),
            Warning = limited,
        });
        Assert.Equal(limited, vm.RoadmapStatusText);

        // Sin aviso, el texto corto de siempre.
        vm.ApplyRoadmapResult(new MilestoneFetchResult { Milestones = TestFeedback.Milestones(), Source = ReleaseFetchSource.OfflineCache });
        Assert.Equal("Sin conexión · datos guardados", vm.RoadmapStatusText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Roadmap_Refresh_RightAfterChecking_DoesNotAskGitHubAgain() => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        vm.ApplyRoadmapResult(TestFeedback.Result()); // recién comprobado (FetchedAt = ahora)
        await ((AsyncRelayCommand)vm.RefreshRoadmapCommand).ExecuteAsync(null);
        Assert.Equal("Comprobado hace un momento.", vm.ToastText);
        Assert.False(vm.IsRoadmapStale);
        Assert.StartsWith("Comprobado a las ", vm.RoadmapStatusText);
    });

    [Fact]
    public Task Roadmap_WithoutMilestones_ShowsTheEmptyState_AndHidesTheHomeCard() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.ApplyRoadmapResult(TestFeedback.Result(Array.Empty<MilestoneInfo>()));
        Assert.True(vm.IsRoadmapEmpty);
        Assert.False(vm.IsRoadmapLoading);
        Assert.False(vm.IsRoadmapFailed);
        Assert.False(vm.HasRoadmapItems);
        Assert.False(vm.HasRoadmapCurrent);
        Assert.Empty(vm.RoadmapPhases);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Roadmap_KeepsExpandedMilestonesAcrossRefreshes() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.ApplyRoadmapResult(TestFeedback.Result());
        var pre2 = vm.RoadmapItems[1];
        pre2.IsExpanded = true;
        Assert.NotNull(pre2.DescriptionDocument);

        vm.ApplyRoadmapResult(TestFeedback.Result());
        Assert.NotSame(pre2, vm.RoadmapItems[1]);
        Assert.True(vm.RoadmapItems[1].IsExpanded);
        Assert.False(vm.RoadmapItems[0].IsExpanded);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Roadmap_NavigatingThere_SelectsThePage() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        vm.ShowRoadmapCommand.Execute(null);
        Assert.True(vm.IsRoadmapPage);
        Assert.Equal(AppPage.Roadmap, vm.CurrentPage);
        vm.NavigateCommand.Execute("Home");
        Assert.False(vm.IsRoadmapPage);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Pre-producción.\n\nPregunta: ¿x?\n\n---\n_Generated by [Claude Code](https://claude.ai/code)_", "Pre-producción.\n\nPregunta: ¿x?")]
    [InlineData("Texto.\n\n---\n\n_Generated with Claude Code_\n\n", "Texto.")]
    [InlineData("Texto.\n_Generated by alguien_", "Texto.")]
    [InlineData("Texto.\n\n---\n\nMás texto.", "Texto.\n\n---\n\nMás texto.")]
    [InlineData("  Solo texto  ", "Solo texto")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Roadmap_Description_DropsTheGeneratedFooter(string? description, string expected)
        => Assert.Equal(expected, RoadmapItemViewModel.CleanDescription(description));

    [Fact]
    public Task RoadmapItem_Dates() => WpfHost.RunAsync(() =>
    {
        var closedAt = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var done = new RoadmapItemViewModel(new RoadmapItem(new MilestoneInfo
        {
            Number = 1, Title = "Pre 1", IsClosed = true, ClosedIssues = 3, ClosedAt = closedAt, DueOn = closedAt.AddDays(5),
        }, MilestoneStatus.Done, "Pre-producción", null), showSeparator: false);
        Assert.Equal("Completado el 30/09/2026", done.DatesText);
        Assert.False(done.CanOpenOnGitHub); // sin URL

        var due = new RoadmapItemViewModel(new RoadmapItem(new MilestoneInfo
        {
            Number = 2, Title = "", DueOn = new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero), HtmlUrl = "https://github.com/x/y/milestone/2",
        }, MilestoneStatus.Planned, "Producción", null), showSeparator: true);
        Assert.Equal("Fecha prevista: 01/12/2026", due.DatesText);
        Assert.Equal("Hito 2", due.Title);
        Assert.True(due.CanOpenOnGitHub);
        Assert.Equal("Hito 2, Planificado", due.AutomationName);
        return Task.CompletedTask;
    });
}
