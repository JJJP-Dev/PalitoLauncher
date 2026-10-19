using System.Windows.Input;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Resultado del diálogo de la encuesta cuando se envió (null si se canceló).</summary>
public sealed record SurveyDialogResult(FeedbackSendOutcome Outcome);

/// <summary>Contexto de la encuesta: de dónde viene, la partida (si la hay) y los datos que se adjuntan al envío.</summary>
public sealed class SurveyContext
{
    public FeedbackTrigger Trigger { get; init; } = FeedbackTrigger.Manual;

    /// <summary>Duración de la partida (si viene de una partida).</summary>
    public int? SessionMinutes { get; init; }

    /// <summary>Código de salida del juego (si viene de una partida y se conoce).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Estrellas elegidas en la invitación: se ponen en la primera pregunta de valoración.</summary>
    public int? InitialRating { get; init; }

    /// <summary>Apodo guardado en los ajustes (prellena el campo).</summary>
    public string? Nickname { get; init; }

    public string? GameVersion { get; init; }
    public string? GameVersionDisplay { get; init; }
    public string LauncherVersion { get; init; } = "";
    public string? OsDescription { get; init; }
}

/// <summary>
/// Diálogo overlay "Tu opinión": las preguntas de la encuesta (una plantilla por tipo), un apodo opcional y Enviar.
/// Enviar se habilita cuando todas las obligatorias están respondidas; mientras envía bloquea el diálogo y si falla
/// muestra el mensaje sin cerrar ni perder nada. Esc/Cancelar piden confirmar solo si ya hay respuestas, y cerrar el
/// launcher con respuestas sin enviar también (<see cref="ConfirmExitAsync"/>). Enter no envía (hay cuadros de texto
/// multilínea).
/// </summary>
public sealed class SurveyDialogViewModel : ObservableObject, IOverlayDialog
{
    /// <summary>Longitud máxima del apodo.</summary>
    public const int NicknameMaxLength = 40;

    internal const string GenericSendError =
        "No se pudo enviar tu opinión. Vuelve a intentarlo: lo que has escrito sigue aquí.";

    internal const string DiscardQuestion = "¿Descartar tus respuestas?";
    internal const string ExitQuestion = "Tienes una opinión sin enviar. ¿Salir sin enviarla?";

    /// <summary>Barra de confirmación que sustituye a los botones del pie.</summary>
    private enum Confirmation
    {
        None,

        /// <summary>Esc/Cancelar con respuestas: [Descartar] [Seguir respondiendo].</summary>
        Discard,

        /// <summary>Cerrar el launcher con respuestas sin enviar: [Salir] [Volver].</summary>
        Exit,
    }

    private readonly SurveyDefinition _survey;
    private readonly SurveyContext _context;
    private readonly Func<FeedbackSubmission, Task<FeedbackSendOutcome>> _submit;
    private readonly TaskCompletionSource<SurveyDialogResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _exitConfirmation;
    private string _nickname;
    private bool _isSending;
    private Confirmation _confirmation;
    private string? _errorMessage;

    public SurveyDialogViewModel(SurveyDefinition survey, SurveyContext context, Func<FeedbackSubmission, Task<FeedbackSendOutcome>> submit)
    {
        _survey = survey ?? throw new ArgumentNullException(nameof(survey));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));

        var questions = new List<SurveyQuestionViewModel>();
        foreach (var question in survey.Questions ?? new List<SurveyQuestion>())
        {
            var vm = SurveyQuestionViewModel.Create(question, questions.Count + 1);
            if (vm == null) continue;
            vm.AnswerChanged += OnAnswerChanged;
            questions.Add(vm);
        }
        Questions = questions;

        // La valoración elegida en la invitación va a la primera pregunta de estrellas.
        if (context.InitialRating is int rating and >= 1 and <= 5
            && Questions.OfType<RatingQuestionViewModel>().FirstOrDefault() is { } first)
        {
            first.Value = rating;
        }

        _nickname = (context.Nickname ?? "").Trim();
        if (_nickname.Length > NicknameMaxLength) _nickname = _nickname[..NicknameMaxLength];

        Title = string.IsNullOrWhiteSpace(survey.Title) ? $"Tu opinión sobre {LauncherConfig.GameDisplayName}" : survey.Title.Trim();
        Intro = string.IsNullOrWhiteSpace(survey.Intro) ? null : survey.Intro.Trim();
        MetaText = BuildMetaText(context.GameVersionDisplay, Questions);
        FooterNote = $"Se enviará al equipo de {LauncherConfig.GameDisplayName} junto con la versión del juego y de Windows (y tu nombre si lo escribes). No se envía nada más.";

        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        CancelCommand = new RelayCommand(Cancel, () => !IsSending);
        DiscardCommand = new RelayCommand(Discard, () => !IsSending);
        KeepEditingCommand = new RelayCommand(KeepEditing);
    }

    public string Title { get; }
    public string? Intro { get; }
    public bool HasIntro => Intro != null;

    /// <summary>"Versión 0.0.3 · Prototipo 2 · ~1 minuto".</summary>
    public string MetaText { get; }

    public string FooterNote { get; }

    public IReadOnlyList<SurveyQuestionViewModel> Questions { get; }

    public FeedbackTrigger Trigger => _context.Trigger;
    public bool IsDanger => false;

    public string Nickname
    {
        get => _nickname;
        set
        {
            var text = value ?? "";
            if (text.Length > NicknameMaxLength) text = text[..NicknameMaxLength];
            SetProperty(ref _nickname, text);
        }
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (!SetProperty(ref _isSending, value)) return;
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(SendButtonText));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(ShowMissingRequired));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Las preguntas se pueden tocar (no mientras se envía).</summary>
    public bool IsInputEnabled => !_isSending;

    public string SendButtonText => _isSending ? "Enviando…" : "Enviar";

    /// <summary>Barra "¿Descartar tus respuestas?" en lugar de los botones (Esc/Cancelar con respuestas).</summary>
    public bool IsConfirmingDiscard => _confirmation == Confirmation.Discard;

    /// <summary>Barra "Tienes una opinión sin enviar. ¿Salir sin enviarla?" (se intentó cerrar el launcher).</summary>
    public bool IsConfirmingExit => _confirmation == Confirmation.Exit;

    /// <summary>Se ve una de las dos barras de confirmación (en lugar de Enviar/Cancelar).</summary>
    public bool IsConfirming => _confirmation != Confirmation.None;

    public bool IsFooterButtonsVisible => !IsConfirming;

    /// <summary>Pregunta de la barra de confirmación.</summary>
    public string ConfirmText => _confirmation == Confirmation.Exit ? ExitQuestion : DiscardQuestion;

    /// <summary>Botón que descarta las respuestas: «Descartar» o «Salir».</summary>
    public string ConfirmActionText => _confirmation == Confirmation.Exit ? "Salir" : "Descartar";

    /// <summary>Botón que vuelve a la encuesta: «Seguir respondiendo» o «Volver».</summary>
    public string KeepEditingText => _confirmation == Confirmation.Exit ? "Volver" : "Seguir respondiendo";

    /// <summary>
    /// Hay respuestas que se perderían al cerrar el launcher: ni se están enviando (ya están en la bandeja) ni la encuesta
    /// se cerró ya.
    /// </summary>
    public bool HasUnsentAnswers => HasAnswers && !_isSending && !_completion.Task.IsCompleted;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    /// <summary>Preguntas obligatorias sin responder.</summary>
    public int MissingRequiredCount => Questions.Count(q => q.IsRequired && !q.IsAnswered);

    public bool CanSend => !_isSending && !IsConfirming && MissingRequiredCount == 0 && Questions.Count > 0;

    /// <summary>"Falta 1 pregunta obligatoria (*)" / "Faltan 2 preguntas obligatorias (*)" / "".</summary>
    public string MissingRequiredText => MissingRequiredCount switch
    {
        0 => "",
        1 => "Falta 1 pregunta obligatoria (*)",
        var n => $"Faltan {n} preguntas obligatorias (*)",
    };

    public bool ShowMissingRequired => !_isSending && MissingRequiredCount > 0;

    /// <summary>Hay alguna respuesta (incluida la valoración traída de la invitación): cancelar pide confirmación.</summary>
    public bool HasAnswers => Questions.Any(q => q.IsAnswered);

    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand KeepEditingCommand { get; }

    public Task<SurveyDialogResult?> Completion => _completion.Task;

    /// <summary>
    /// Esc o Cancelar: con respuestas pide confirmar (Esc otra vez = seguir respondiendo, también en «¿Salir sin
    /// enviarla?»). Mientras envía, nada.
    /// </summary>
    public void Cancel()
    {
        if (_isSending) return;
        if (IsConfirming)
        {
            KeepEditing();
            return;
        }
        if (HasAnswers)
        {
            SetConfirmation(Confirmation.Discard);
            return;
        }
        Complete(null);
    }

    /// <summary>
    /// Se va a cerrar el launcher. Con respuestas sin enviar muestra «Tienes una opinión sin enviar. ¿Salir sin enviarla?»
    /// y devuelve true si el usuario elige «Salir» (la encuesta se descarta) o false con «Volver»/Esc. Sin nada que perder
    /// (sin respuestas, enviándose o ya cerrada) devuelve true al momento.
    /// </summary>
    public Task<bool> ConfirmExitAsync()
    {
        if (!HasUnsentAnswers) return Task.FromResult(true);
        if (_exitConfirmation is { } pending) return pending.Task;

        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitConfirmation = confirmation;
        SetConfirmation(Confirmation.Exit);
        return confirmation.Task;
    }

    /// <summary>Cierra la encuesta sin enviar ni preguntar (solo si no hay respuestas que perder). True si se cerró.</summary>
    internal bool CloseIfEmpty()
    {
        if (_isSending || HasAnswers) return false;
        Complete(null);
        return true;
    }

    /// <summary>Enter no envía: hay cuadros de texto multilínea y se enviaría sin querer.</summary>
    public void Accept()
    {
    }

    /// <summary>Construye la respuesta tal como se enviaría (preguntas mostradas, respuestas por Id, apodo…).</summary>
    internal FeedbackSubmission BuildSubmission()
    {
        var answers = new Dictionary<string, SurveyAnswer>(StringComparer.Ordinal);
        foreach (var question in Questions)
        {
            var answer = question.BuildAnswer();
            if (answer != null && !answer.IsEmpty && !string.IsNullOrEmpty(question.Id)) answers[question.Id] = answer;
        }

        var nickname = (_nickname ?? "").Trim();
        bool fromSession = _context.Trigger == FeedbackTrigger.AfterSession;
        return new FeedbackSubmission
        {
            SurveyId = _survey.Id ?? "",
            SurveyTitle = Title,
            Questions = Questions.Select(q => q.Question).ToList(),
            Answers = answers,
            Nickname = nickname.Length > 0 ? nickname : null,
            Trigger = _context.Trigger,
            GameVersion = string.IsNullOrWhiteSpace(_context.GameVersion) ? null : _context.GameVersion,
            GameVersionDisplay = string.IsNullOrWhiteSpace(_context.GameVersionDisplay) ? null : _context.GameVersionDisplay,
            LauncherVersion = _context.LauncherVersion ?? "",
            OsDescription = _context.OsDescription,
            SessionMinutes = fromSession ? _context.SessionMinutes : null,
            ExitCode = fromSession ? _context.ExitCode : null,
        };
    }

    private async Task SendAsync()
    {
        if (!CanSend) return;
        ErrorMessage = null;
        IsSending = true;
        try
        {
            var outcome = await _submit(BuildSubmission());
            Complete(new SurveyDialogResult(outcome));
        }
        catch (LauncherException ex)
        {
            Log.Warn($"No se pudo enviar la opinión: {ex.UserMessage}", ex);
            ErrorMessage = ex.UserMessage;
        }
        catch (OperationCanceledException)
        {
            Log.Info("Envío de la opinión cancelado.");
            ErrorMessage = GenericSendError;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al enviar la opinión.", ex);
            ErrorMessage = GenericSendError;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>«Descartar» o «Salir»: cierra sin enviar.</summary>
    private void Discard()
    {
        if (_isSending) return;
        Complete(null);
    }

    /// <summary>«Seguir respondiendo» o «Volver» (en «¿Salir sin enviarla?» el launcher no se cierra).</summary>
    private void KeepEditing()
    {
        ResolveExitConfirmation(leave: false);
        SetConfirmation(Confirmation.None);
    }

    /// <summary>Cierra el diálogo; si alguien esperaba la respuesta a «¿Salir sin enviarla?», ya no queda nada que perder.</summary>
    private void Complete(SurveyDialogResult? result)
    {
        _completion.TrySetResult(result);
        ResolveExitConfirmation(leave: true);
    }

    private void ResolveExitConfirmation(bool leave)
    {
        var pending = _exitConfirmation;
        _exitConfirmation = null;
        pending?.TrySetResult(leave);
    }

    private void SetConfirmation(Confirmation value)
    {
        if (_confirmation == value) return;
        _confirmation = value;
        OnPropertyChanged(nameof(IsConfirmingDiscard));
        OnPropertyChanged(nameof(IsConfirmingExit));
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(IsFooterButtonsVisible));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(ConfirmActionText));
        OnPropertyChanged(nameof(KeepEditingText));
        OnPropertyChanged(nameof(CanSend));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void OnAnswerChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(MissingRequiredCount));
        OnPropertyChanged(nameof(MissingRequiredText));
        OnPropertyChanged(nameof(ShowMissingRequired));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(HasAnswers));
        RelayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>"Versión 0.0.3 · Prototipo 2 · ~1 minuto" (sin juego instalado, solo el tiempo).</summary>
    internal static string BuildMetaText(string? gameVersionDisplay, IReadOnlyList<SurveyQuestionViewModel> questions)
    {
        // ~6 s por pregunta rápida y ~20 s por cada texto libre.
        int texts = questions.Count(q => q is TextQuestionViewModel);
        double seconds = (questions.Count - texts) * 6 + texts * 20;
        int minutes = Math.Max(1, (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero));
        var time = minutes == 1 ? "~1 minuto" : $"~{minutes} minutos";
        return string.IsNullOrWhiteSpace(gameVersionDisplay) ? time : $"Versión {gameVersionDisplay.Trim()} · {time}";
    }
}
