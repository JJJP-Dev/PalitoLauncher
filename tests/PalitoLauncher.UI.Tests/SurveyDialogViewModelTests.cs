using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Util;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Diálogo de la encuesta: obligatorias, reglas de selección (única, múltiple con «Ninguno» excluyente), construcción de
/// la respuesta, prellenado desde la invitación, envío (éxito, error esperable sin perder nada) y cancelar/Enter. El envío
/// se inyecta (sin red ni FeedbackService).
/// </summary>
public sealed class SurveyDialogViewModelTests
{
    private static readonly SurveyContext ManualContext = new()
    {
        Trigger = FeedbackTrigger.Manual,
        LauncherVersion = "1.0.3",
        OsDescription = "Microsoft Windows 10.0.26200",
        GameVersion = "0.0.3-prototype.2",
        GameVersionDisplay = "0.0.3 · Prototipo 2",
    };

    private static SurveyDialogViewModel Create(SurveyContext? context = null, SurveyDefinition? survey = null,
        Func<FeedbackSubmission, Task<FeedbackSendOutcome>>? submit = null)
        => new(survey ?? TestFeedback.Survey(), context ?? ManualContext,
            submit ?? (_ => Task.FromResult(FeedbackSendOutcome.Sent)));

    private static T Q<T>(SurveyDialogViewModel vm, string id) where T : SurveyQuestionViewModel
        => (T)vm.Questions.Single(q => q.Id == id);

    private static void AnswerRequired(SurveyDialogViewModel vm)
    {
        Q<RatingQuestionViewModel>(vm, "overall").Value = 4;
        Q<ChoiceQuestionViewModel>(vm, "again").Options[1].IsSelected = true;
    }

    private static Task SendAsync(SurveyDialogViewModel vm) => ((AsyncRelayCommand)vm.SendCommand).ExecuteAsync(null);

    // ───────────── Estructura ─────────────

    [Fact]
    public Task Questions_AreCreatedPerType_NumberedAndMarked() => WpfHost.RunAsync(() =>
    {
        var vm = Create();
        Assert.Collection(vm.Questions,
            q => Assert.IsType<RatingQuestionViewModel>(q),
            q => Assert.IsType<ChoiceQuestionViewModel>(q),
            q => Assert.IsType<ScaleQuestionViewModel>(q),
            q => Assert.IsType<MultiQuestionViewModel>(q),
            q => Assert.IsType<TextQuestionViewModel>(q),
            q => Assert.IsType<TextQuestionViewModel>(q));
        Assert.Equal(new[] { "1.", "2.", "3.", "4.", "5.", "6." }, vm.Questions.Select(q => q.NumberText));

        var overall = vm.Questions[0];
        Assert.True(overall.IsRequired);
        Assert.Equal(" *", overall.RequiredMarker);
        Assert.Equal("En general, ¿qué tal la partida? (obligatoria)", overall.AutomationName);
        Assert.Equal("", vm.Questions[2].RequiredMarker);
        Assert.True(vm.Questions[2].HasHint);

        Assert.Equal("Tu opinión sobre Coop2D", vm.Title);
        Assert.True(vm.HasIntro);
        Assert.Equal("Versión 0.0.3 · Prototipo 2 · ~1 minuto", vm.MetaText);
        Assert.False(vm.IsDanger);
        return Task.CompletedTask;
    });

    [Fact]
    public Task UnknownQuestionTypes_AreSkipped_AndNumberingStaysSequential() => WpfHost.RunAsync(() =>
    {
        var survey = new SurveyDefinition
        {
            Id = "x",
            Title = "",
            Questions = new List<SurveyQuestion>
            {
                new() { Id = "a", Type = (SurveyQuestionType)99, Text = "¿Rara?" },
                new() { Id = "b", Type = SurveyQuestionType.Rating, Text = "¿Bien?" },
                new() { Id = "c", Type = SurveyQuestionType.Text, Text = "¿Algo más?" },
            },
        };
        var vm = Create(survey: survey);
        Assert.Equal(new[] { "b", "c" }, vm.Questions.Select(q => q.Id));
        Assert.Equal(new[] { "1.", "2." }, vm.Questions.Select(q => q.NumberText));
        Assert.Equal("Tu opinión sobre Coop2D", vm.Title); // título vacío → por defecto
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(null, 6, 0, "~1 minuto")]
    [InlineData("0.0.3 · Prototipo 2", 6, 0, "Versión 0.0.3 · Prototipo 2 · ~1 minuto")]
    [InlineData(null, 8, 4, "~2 minutos")]
    [InlineData("  ", 1, 0, "~1 minuto")]
    public Task MetaText_ShowsVersionAndEstimatedTime(string? version, int quick, int texts, string expected) => WpfHost.RunAsync(() =>
    {
        var questions = Enumerable.Range(0, quick)
            .Select(i => (SurveyQuestionViewModel)new RatingQuestionViewModel(new SurveyQuestion { Id = $"r{i}", Text = "?" }, i + 1))
            .Concat(Enumerable.Range(0, texts).Select(i =>
                (SurveyQuestionViewModel)new TextQuestionViewModel(new SurveyQuestion { Id = $"t{i}", Type = SurveyQuestionType.Text, Text = "?" }, i + 1)))
            .ToList();
        Assert.Equal(expected, SurveyDialogViewModel.BuildMetaText(version, questions));
        return Task.CompletedTask;
    });

    // ───────────── Obligatorias ─────────────

    [Fact]
    public Task Send_IsDisabledUntilEveryRequiredQuestionIsAnswered() => WpfHost.RunAsync(() =>
    {
        var vm = Create();
        Assert.False(vm.CanSend);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.Equal(2, vm.MissingRequiredCount);
        Assert.Equal("Faltan 2 preguntas obligatorias (*)", vm.MissingRequiredText);
        Assert.True(vm.ShowMissingRequired);

        // Las opcionales no cuentan.
        Q<TextQuestionViewModel>(vm, "best").AnswerText = "Las reacciones";
        Assert.Equal(2, vm.MissingRequiredCount);

        Q<RatingQuestionViewModel>(vm, "overall").Value = 5;
        Assert.Equal("Falta 1 pregunta obligatoria (*)", vm.MissingRequiredText);
        Assert.False(vm.CanSend);

        var again = Q<ChoiceQuestionViewModel>(vm, "again");
        again.Options[0].IsSelected = true;
        Assert.True(vm.CanSend);
        Assert.True(vm.SendCommand.CanExecute(null));
        Assert.Equal("", vm.MissingRequiredText);
        Assert.False(vm.ShowMissingRequired);

        // Quitar una respuesta obligatoria vuelve a deshabilitar Enviar.
        again.Options[0].IsSelected = false;
        Assert.False(vm.CanSend);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("", false)]
    [InlineData("   \n\t ", false)]
    [InlineData(" hola ", true)]
    public Task RequiredText_WhitespaceDoesNotCount(string text, bool answered) => WpfHost.RunAsync(() =>
    {
        var survey = new SurveyDefinition
        {
            Id = "t",
            Title = "T",
            Questions = new List<SurveyQuestion> { new() { Id = "why", Type = SurveyQuestionType.Text, Text = "¿Por qué?", Required = true } },
        };
        var vm = Create(survey: survey);
        Q<TextQuestionViewModel>(vm, "why").AnswerText = text;
        Assert.Equal(answered, vm.CanSend);
        Assert.Equal(answered, vm.HasAnswers);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Rating_IsClampedToZeroToFive() => WpfHost.RunAsync(() =>
    {
        var rating = Q<RatingQuestionViewModel>(Create(), "overall");
        rating.Value = 9;
        Assert.Equal(5, rating.Value);
        rating.Value = -3;
        Assert.Equal(0, rating.Value);
        Assert.False(rating.IsAnswered);
        Assert.Null(rating.BuildAnswer());
        return Task.CompletedTask;
    });

    // ───────────── Selección ─────────────

    [Fact]
    public Task Choice_IsSingleSelection() => WpfHost.RunAsync(() =>
    {
        var again = Q<ChoiceQuestionViewModel>(Create(), "again");
        again.Options[0].IsSelected = true;
        again.Options[2].IsSelected = true;
        Assert.Equal(new[] { false, false, true }, again.Options.Select(o => o.IsSelected));
        Assert.Equal("No mucho", again.Selected);
        Assert.Equal(new[] { "No mucho" }, again.BuildAnswer()!.Choices);
        Assert.Single(again.Options.Select(o => o.GroupName).Distinct()); // un grupo de RadioButton por pregunta
        return Task.CompletedTask;
    });

    [Fact]
    public Task Scale_HasMinToMaxButtonsWithEndLabels_AndSingleSelection() => WpfHost.RunAsync(() =>
    {
        var scale = Q<ScaleQuestionViewModel>(Create(), "difficulty");
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, scale.Options.Select(o => o.Text));
        Assert.Equal("1 de 5: Muy fácil", scale.Options[0].AutomationName);
        Assert.Equal("3 de 5", scale.Options[2].AutomationName);
        Assert.True(scale.HasLabels);

        scale.Options[3].IsSelected = true;
        scale.Options[1].IsSelected = true;
        Assert.Equal(2, scale.Value);
        Assert.Equal(2, scale.BuildAnswer()!.Number);
        Assert.Single(scale.Options, o => o.IsSelected);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0, 10, 11)]
    [InlineData(5, 5, 5)]   // Min == Max (sin sanear) → 1..5
    [InlineData(3, 1, 5)]   // Min > Max → 1..5
    public Task Scale_RangeIsProtected(int min, int max, int expectedButtons) => WpfHost.RunAsync(() =>
    {
        var scale = new ScaleQuestionViewModel(new SurveyQuestion { Id = "s", Type = SurveyQuestionType.Scale, Text = "?", Min = min, Max = max }, 1);
        Assert.Equal(expectedButtons, scale.Options.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Multi_NoneOptionIsExclusive_BothWays() => WpfHost.RunAsync(() =>
    {
        var issues = Q<MultiQuestionViewModel>(Create(), "issues");
        var lag = issues.Options[0];
        var visual = issues.Options[2];
        var none = issues.Options[3];
        Assert.True(none.IsExclusive);
        Assert.False(lag.IsExclusive);

        lag.IsSelected = true;
        visual.IsSelected = true;
        Assert.Equal(new[] { "Lag o desconexiones", "Fallos visuales" }, issues.Selected);

        // «Ninguno» quita las demás.
        none.IsSelected = true;
        Assert.Equal(new[] { "Ninguno" }, issues.Selected);
        Assert.False(lag.IsSelected);
        Assert.False(visual.IsSelected);

        // Elegir otra quita «Ninguno».
        visual.IsSelected = true;
        Assert.Equal(new[] { "Fallos visuales" }, issues.Selected);
        Assert.False(none.IsSelected);

        // Varias normales a la vez sí.
        lag.IsSelected = true;
        Assert.Equal(new[] { "Lag o desconexiones", "Fallos visuales" }, issues.BuildAnswer()!.Choices);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Ninguno", true)]
    [InlineData("Ninguna", true)]
    [InlineData("  ninguno. ", true)]
    [InlineData("NINGUNA!", true)]
    [InlineData("Ninguno de estos me pasó", false)]
    [InlineData("Lag", false)]
    [InlineData(null, false)]
    public void Multi_DetectsTheNoneOption(string? option, bool expected)
        => Assert.Equal(expected, MultiQuestionViewModel.IsNoneOption(option));

    [Fact]
    public Task Text_CountsCharactersAndRespectsMaxLength() => WpfHost.RunAsync(() =>
    {
        var vm = Create();
        var improve = Q<TextQuestionViewModel>(vm, "improve");
        Assert.Equal(50, improve.MaxLength);
        Assert.Equal("0 / 50", improve.CounterText);
        Assert.False(improve.IsNearLimit);

        improve.AnswerText = new string('a', 60);
        Assert.Equal(50, improve.AnswerText.Length);
        Assert.Equal("50 / 50", improve.CounterText);
        Assert.True(improve.IsNearLimit);

        var best = Q<TextQuestionViewModel>(vm, "best");
        Assert.Equal(1000, best.MaxLength);
        Assert.True(best.ShowPlaceholder);
        best.AnswerText = "x";
        Assert.False(best.ShowPlaceholder);
        return Task.CompletedTask;
    });

    // ───────────── Respuesta ─────────────

    [Fact]
    public Task BuildSubmission_HasShownQuestions_OnlyAnsweredAnswers_AndContext() => WpfHost.RunAsync(() =>
    {
        var survey = TestFeedback.Survey();
        var context = new SurveyContext
        {
            Trigger = FeedbackTrigger.AfterSession,
            SessionMinutes = 34,
            ExitCode = 0,
            Nickname = "Palito",
            GameVersion = "0.0.3-prototype.2",
            GameVersionDisplay = "0.0.3 · Prototipo 2",
            LauncherVersion = "1.0.3",
            OsDescription = "Microsoft Windows 10.0.26200",
        };
        var vm = Create(context, survey);
        AnswerRequired(vm);
        Q<ScaleQuestionViewModel>(vm, "difficulty").Options[4].IsSelected = true;
        Q<MultiQuestionViewModel>(vm, "issues").Options[3].IsSelected = true;
        Q<TextQuestionViewModel>(vm, "best").AnswerText = "  Combinar fuego y agua  \n";
        Q<TextQuestionViewModel>(vm, "improve").AnswerText = "   "; // sin responder
        vm.Nickname = "  Juanjo  ";

        var s = vm.BuildSubmission();

        Assert.Equal("test-survey", s.SurveyId);
        Assert.Equal("Tu opinión sobre Coop2D", s.SurveyTitle);
        Assert.Equal(survey.Questions, s.Questions); // las preguntas tal como se mostraron, en orden
        Assert.Equal(new[] { "again", "best", "difficulty", "issues", "overall" }, s.Answers.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(4, s.Answers["overall"].Number);
        Assert.Equal(new[] { "Sí, otro día" }, s.Answers["again"].Choices);
        Assert.Equal(5, s.Answers["difficulty"].Number);
        Assert.Equal(new[] { "Ninguno" }, s.Answers["issues"].Choices);
        Assert.Equal("Combinar fuego y agua", s.Answers["best"].Text);
        Assert.All(s.Answers.Values, a => Assert.False(a.IsEmpty));

        Assert.Equal("Juanjo", s.Nickname);
        Assert.Equal(FeedbackTrigger.AfterSession, s.Trigger);
        Assert.Equal("0.0.3-prototype.2", s.GameVersion);
        Assert.Equal("0.0.3 · Prototipo 2", s.GameVersionDisplay);
        Assert.Equal("1.0.3", s.LauncherVersion);
        Assert.Equal("Microsoft Windows 10.0.26200", s.OsDescription);
        Assert.Equal(34, s.SessionMinutes);
        Assert.Equal(0, s.ExitCode);
        Assert.Equal(32, s.Id.Length); // Guid "N"
        return Task.CompletedTask;
    });

    [Fact]
    public Task BuildSubmission_Manual_WithoutGame_HasNoSessionDataNorVersion() => WpfHost.RunAsync(() =>
    {
        var vm = Create(new SurveyContext
        {
            Trigger = FeedbackTrigger.Manual,
            SessionMinutes = 10, // se ignora: no viene de una partida
            ExitCode = 3,
            LauncherVersion = "1.0.3",
        });
        AnswerRequired(vm);
        vm.Nickname = "   ";

        var s = vm.BuildSubmission();
        Assert.Equal(FeedbackTrigger.Manual, s.Trigger);
        Assert.Null(s.SessionMinutes);
        Assert.Null(s.ExitCode);
        Assert.Null(s.Nickname);
        Assert.Null(s.GameVersion);
        Assert.Null(s.GameVersionDisplay);
        Assert.Equal("~1 minuto", vm.MetaText);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Nickname_IsPrefilledFromSettings_AndLimited() => WpfHost.RunAsync(() =>
    {
        var vm = Create(new SurveyContext { Nickname = "  Palito  ", LauncherVersion = "1.0.3" });
        Assert.Equal("Palito", vm.Nickname);
        vm.Nickname = new string('n', 100);
        Assert.Equal(SurveyDialogViewModel.NicknameMaxLength, vm.Nickname.Length);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(4, 4)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(null, 0)]
    public Task InitialRating_FromThePrompt_GoesToTheFirstRatingQuestionOnly(int? initial, int expected) => WpfHost.RunAsync(() =>
    {
        var survey = TestFeedback.Survey();
        survey.Questions.Add(new SurveyQuestion { Id = "fun", Type = SurveyQuestionType.Rating, Text = "¿Divertido?" });
        var vm = Create(new SurveyContext { Trigger = FeedbackTrigger.AfterSession, InitialRating = initial, LauncherVersion = "1.0.3" }, survey);

        Assert.Equal(expected, Q<RatingQuestionViewModel>(vm, "overall").Value);
        Assert.Equal(0, Q<RatingQuestionViewModel>(vm, "fun").Value);
        Assert.Equal(expected > 0 ? 1 : 2, vm.MissingRequiredCount);
        if (expected > 0) Assert.Equal(expected, vm.BuildSubmission().Answers["overall"].Number);
        return Task.CompletedTask;
    });

    // ───────────── Envío ─────────────

    [Fact]
    public Task Send_Success_ClosesWithTheOutcome() => WpfHost.RunAsync(async () =>
    {
        FeedbackSubmission? received = null;
        var vm = Create(submit: s =>
        {
            received = s;
            return Task.FromResult(FeedbackSendOutcome.Queued);
        });
        AnswerRequired(vm);

        await SendAsync(vm);

        Assert.True(vm.Completion.IsCompleted);
        Assert.Equal(FeedbackSendOutcome.Queued, (await vm.Completion)!.Outcome);
        Assert.NotNull(received);
        Assert.Equal(4, received!.Answers["overall"].Number);
    });

    [Fact]
    public Task Send_IsIgnoredWhileRequiredAreMissing() => WpfHost.RunAsync(async () =>
    {
        int calls = 0;
        var vm = Create(submit: _ =>
        {
            calls++;
            return Task.FromResult(FeedbackSendOutcome.Sent);
        });
        await SendAsync(vm);
        Assert.Equal(0, calls);
        Assert.False(vm.Completion.IsCompleted);
    });

    [Fact]
    public Task Send_ExpectedError_StaysOpenWithTheMessage_AndKeepsEverything() => WpfHost.RunAsync(async () =>
    {
        int calls = 0;
        var vm = Create(submit: _ =>
        {
            calls++;
            if (calls == 1) throw new LauncherException("Espera unos segundos antes de enviar otra opinión.");
            return Task.FromResult(FeedbackSendOutcome.Sent);
        });
        AnswerRequired(vm);
        Q<TextQuestionViewModel>(vm, "best").AnswerText = "Todo";
        vm.Nickname = "Palito";

        await SendAsync(vm);

        Assert.False(vm.Completion.IsCompleted);
        Assert.True(vm.HasError);
        Assert.Equal("Espera unos segundos antes de enviar otra opinión.", vm.ErrorMessage);
        Assert.False(vm.IsSending);
        Assert.True(vm.IsInputEnabled);
        Assert.Equal("Enviar", vm.SendButtonText);
        Assert.Equal(4, Q<RatingQuestionViewModel>(vm, "overall").Value);
        Assert.Equal("Todo", Q<TextQuestionViewModel>(vm, "best").AnswerText);
        Assert.Equal("Palito", vm.Nickname);
        Assert.True(vm.CanSend);

        // Reintentar: esta vez sale bien y el error desaparece.
        await SendAsync(vm);
        Assert.False(vm.HasError);
        Assert.Equal(FeedbackSendOutcome.Sent, (await vm.Completion)!.Outcome);
    });

    [Fact]
    public Task Send_UnexpectedError_ShowsAGenericMessage() => WpfHost.RunAsync(async () =>
    {
        var vm = Create(submit: _ => throw new InvalidOperationException("boom"));
        AnswerRequired(vm);
        await SendAsync(vm);
        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal(SurveyDialogViewModel.GenericSendError, vm.ErrorMessage);
        Assert.DoesNotContain("boom", vm.ErrorMessage);
    });

    [Fact]
    public Task WhileSending_TheDialogIsLocked() => WpfHost.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource<FeedbackSendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(submit: _ => gate.Task);
        AnswerRequired(vm);

        var sending = SendAsync(vm);
        Assert.True(vm.IsSending);
        Assert.False(vm.IsInputEnabled);
        Assert.Equal("Enviando…", vm.SendButtonText);
        Assert.False(vm.CanSend);
        Assert.False(vm.SendCommand.CanExecute(null));
        Assert.False(vm.CancelCommand.CanExecute(null));

        vm.Cancel(); // Esc mientras envía: nada
        Assert.False(vm.IsConfirmingDiscard);
        Assert.False(vm.Completion.IsCompleted);

        gate.SetResult(FeedbackSendOutcome.Sent);
        await sending;
        Assert.Equal(FeedbackSendOutcome.Sent, (await vm.Completion)!.Outcome);
    });

    // ───────────── Cancelar / Enter ─────────────

    [Fact]
    public Task Cancel_WithoutAnswers_ClosesAtOnce() => WpfHost.RunAsync(async () =>
    {
        var vm = Create(new SurveyContext { Nickname = "Palito", LauncherVersion = "1.0.3" }); // el apodo no cuenta como respuesta
        vm.Cancel();
        Assert.False(vm.IsConfirmingDiscard);
        Assert.Null(await vm.Completion);
    });

    [Fact]
    public Task Cancel_WithAnswers_AsksToConfirm_EscAgainKeepsEditing() => WpfHost.RunAsync(async () =>
    {
        var vm = Create();
        Q<TextQuestionViewModel>(vm, "best").AnswerText = "Algo";
        Assert.True(vm.HasAnswers);

        vm.Cancel();
        Assert.True(vm.IsConfirmingDiscard);
        Assert.False(vm.IsFooterButtonsVisible);
        Assert.False(vm.Completion.IsCompleted);

        vm.Cancel(); // Esc en la confirmación = seguir respondiendo
        Assert.False(vm.IsConfirmingDiscard);
        Assert.True(vm.IsFooterButtonsVisible);

        vm.CancelCommand.Execute(null);
        Assert.True(vm.IsConfirmingDiscard);
        vm.KeepEditingCommand.Execute(null);
        Assert.False(vm.IsConfirmingDiscard);
        Assert.Equal("Algo", Q<TextQuestionViewModel>(vm, "best").AnswerText);

        vm.Cancel();
        vm.DiscardCommand.Execute(null);
        Assert.Null(await vm.Completion);
    });

    // ───────────── Cerrar el launcher con la encuesta abierta ─────────────

    [Fact]
    public Task ConfirmExit_WithoutAnswers_LeavesAtOnce() => WpfHost.RunAsync(async () =>
    {
        var vm = Create(new SurveyContext { Nickname = "Palito", LauncherVersion = "1.0.3" });
        Assert.False(vm.HasUnsentAnswers);
        Assert.True(await vm.ConfirmExitAsync());
        Assert.False(vm.IsConfirming);
    });

    [Fact]
    public Task ConfirmExit_WithAnswers_AsksSalirOVolver() => WpfHost.RunAsync(async () =>
    {
        var vm = Create();
        Q<TextQuestionViewModel>(vm, "improve").AnswerText = "Más mapas";
        Assert.True(vm.HasUnsentAnswers);

        // «Volver»: la encuesta sigue abierta con lo escrito.
        var first = vm.ConfirmExitAsync();
        Assert.False(first.IsCompleted);
        Assert.True(vm.IsConfirmingExit);
        Assert.True(vm.IsConfirming);
        Assert.False(vm.IsConfirmingDiscard);
        Assert.False(vm.IsFooterButtonsVisible);
        Assert.False(vm.CanSend);
        Assert.Equal("Tienes una opinión sin enviar. ¿Salir sin enviarla?", vm.ConfirmText);
        Assert.Equal("Salir", vm.ConfirmActionText);
        Assert.Equal("Volver", vm.KeepEditingText);
        Assert.Same(first, vm.ConfirmExitAsync()); // pulsar X otra vez no apila otra pregunta

        vm.KeepEditingCommand.Execute(null);
        Assert.False(await first);
        Assert.False(vm.IsConfirming);
        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal("Más mapas", Q<TextQuestionViewModel>(vm, "improve").AnswerText);

        // Esc en la pregunta = «Volver».
        var second = vm.ConfirmExitAsync();
        vm.Cancel();
        Assert.False(await second);
        Assert.False(vm.Completion.IsCompleted);

        // «Salir»: se descarta y se puede cerrar.
        var third = vm.ConfirmExitAsync();
        vm.DiscardCommand.Execute(null);
        Assert.True(await third);
        Assert.Null(await vm.Completion);
        Assert.False(vm.HasUnsentAnswers);
        Assert.True(await vm.ConfirmExitAsync());
    });

    [Fact]
    public Task ConfirmExit_FromTheDiscardBar_SwitchesTheQuestion() => WpfHost.RunAsync(async () =>
    {
        var vm = Create();
        AnswerRequired(vm);
        vm.Cancel();
        Assert.True(vm.IsConfirmingDiscard);
        Assert.Equal("¿Descartar tus respuestas?", vm.ConfirmText);
        Assert.Equal("Descartar", vm.ConfirmActionText);
        Assert.Equal("Seguir respondiendo", vm.KeepEditingText);

        var exit = vm.ConfirmExitAsync();
        Assert.True(vm.IsConfirmingExit);
        Assert.Equal("Tienes una opinión sin enviar. ¿Salir sin enviarla?", vm.ConfirmText);
        vm.DiscardCommand.Execute(null);
        Assert.True(await exit);
    });

    [Fact]
    public Task ConfirmExit_WhileSending_DoesNotAsk() => WpfHost.RunAsync(async () =>
    {
        // Mientras se envía ya está guardada en la bandeja de salida: cerrar no pierde nada.
        var gate = new TaskCompletionSource<FeedbackSendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(submit: _ => gate.Task);
        AnswerRequired(vm);
        var sending = SendAsync(vm);
        Assert.False(vm.HasUnsentAnswers);
        Assert.True(await vm.ConfirmExitAsync());
        Assert.False(vm.IsConfirming);
        gate.SetResult(FeedbackSendOutcome.Queued);
        await sending;
    });

    [Fact]
    public Task CloseIfEmpty_OnlyClosesWithoutAnswers() => WpfHost.RunAsync(async () =>
    {
        var answered = Create();
        Q<RatingQuestionViewModel>(answered, "overall").Value = 2;
        Assert.False(answered.CloseIfEmpty());
        Assert.False(answered.Completion.IsCompleted);

        var empty = Create();
        Assert.True(empty.CloseIfEmpty());
        Assert.Null(await empty.Completion);
    });

    [Fact]
    public Task FooterNote_SaysExactlyWhatIsSent() => WpfHost.RunAsync(() =>
    {
        var vm = Create();
        Assert.Equal("Se enviará al equipo de Coop2D junto con la versión del juego y de Windows (y tu nombre si lo escribes). No se envía nada más.",
            vm.FooterNote);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Enter_NeverSends() => WpfHost.RunAsync(async () =>
    {
        int calls = 0;
        var vm = Create(submit: _ =>
        {
            calls++;
            return Task.FromResult(FeedbackSendOutcome.Sent);
        });
        AnswerRequired(vm);

        vm.Accept();
        await WpfHost.FlushAsync();
        Assert.Equal(0, calls);
        Assert.False(vm.Completion.IsCompleted);
    });
}
