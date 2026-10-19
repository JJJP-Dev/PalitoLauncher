using System.Globalization;
using System.Windows.Input;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;

namespace PalitoLauncher.ViewModels;

/// <summary>Resultado del diálogo «Reportar un problema» cuando se envió (null si se canceló).</summary>
public sealed record BugReportDialogResult(FeedbackSendOutcome Outcome);

/// <summary>Un archivo de la lista «Se adjuntará:» (nombre, qué es y tamaño, ya calculados fuera del hilo de UI).</summary>
public sealed record BugReportAttachmentPreview(string Name, string Description, string SizeText, bool Exists);

/// <summary>De dónde viene el reporte y los datos que se adjuntan al envío.</summary>
public sealed class BugReportContext
{
    /// <summary>Categoría marcada al abrir (Crash desde el aviso de cierre, Launcher desde un diálogo de error…).</summary>
    public BugCategory Category { get; init; } = BugCategory.Bug;

    /// <summary>Texto bajo el título (qué se espera que cuente). Null = el texto general.</summary>
    public string? Intro { get; init; }

    /// <summary>Código de salida del juego (si viene de un cierre inesperado).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Apodo guardado en los ajustes (el mismo que el de las opiniones).</summary>
    public string? Nickname { get; init; }

    public string? GameVersion { get; init; }
    public string? GameVersionDisplay { get; init; }
    public string LauncherVersion { get; init; } = "";
    public string? OsDescription { get; init; }

    /// <summary>Archivos que se adjuntarían si se deja marcada la casilla.</summary>
    public IReadOnlyList<BugReportAttachmentPreview> Attachments { get; init; } = Array.Empty<BugReportAttachmentPreview>();
}

/// <summary>Categoría del reporte como «pill» de selección única.</summary>
public sealed class BugCategoryOptionViewModel : ObservableObject
{
    private readonly Action<BugCategoryOptionViewModel> _onSelected;
    private bool _isSelected;

    internal BugCategoryOptionViewModel(BugCategory category, bool isSelected, Action<BugCategoryOptionViewModel> onSelected)
    {
        Category = category;
        Text = BugReportService.CategoryDisplayName(category);
        _isSelected = isSelected;
        _onSelected = onSelected;
    }

    public BugCategory Category { get; }
    public string Text { get; }

    /// <summary>Grupo de los RadioButton (una sola categoría a la vez).</summary>
    public string GroupName => "bug-category";

    public string AutomationName => $"Tipo de problema: {Text}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            if (value) _onSelected(this);
        }
    }
}

/// <summary>
/// Diálogo overlay «Reportar un problema» (mismo estilo y comportamiento que la encuesta): categoría, qué ha pasado
/// (obligatorio, mínimo 10 caracteres), cómo reproducirlo (opcional), nombre (compartido con las opiniones) y una casilla
/// para adjuntar los registros anonimizados. Enviar bloquea el diálogo y, si falla, muestra el mensaje sin perder nada.
/// Esc/Cancelar piden confirmar si ya hay texto, y cerrar el launcher con un reporte a medias también
/// (<see cref="ConfirmExitAsync"/>). Enter no envía (cuadros multilínea).
/// </summary>
public sealed class BugReportDialogViewModel : ObservableObject, IOverlayDialog
{
    /// <summary>Mínimo de «¿Qué ha pasado?» (como BugReportService).</summary>
    public const int MinDescriptionLength = 10;

    public const int MaxDescriptionLength = 2000;
    public const int MaxStepsLength = 1000;

    internal const string GenericSendError =
        "No se pudo enviar el reporte. Vuelve a intentarlo: lo que has escrito sigue aquí.";

    internal const string DiscardQuestion = "¿Descartar el reporte?";
    internal const string ExitQuestion = "Tienes un reporte sin enviar. ¿Salir sin enviarlo?";

    internal const string DefaultIntro =
        "Cuéntanos qué ha fallado. Cuantos más detalles, más fácil será arreglarlo.";

    private enum Confirmation
    {
        None,

        /// <summary>Esc/Cancelar con texto: [Descartar] [Seguir escribiendo].</summary>
        Discard,

        /// <summary>Cerrar el launcher con el reporte a medias: [Salir] [Volver].</summary>
        Exit,
    }

    private readonly BugReportContext _context;
    private readonly Func<BugReport, bool, Task<FeedbackSendOutcome>> _submit;
    private readonly TaskCompletionSource<BugReportDialogResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _reportId = Guid.NewGuid().ToString("N");
    private TaskCompletionSource<bool>? _exitConfirmation;
    private BugCategory _category;
    private string _description = "";
    private string _steps = "";
    private string _nickname;
    private bool _attachLogs = true;
    private bool _isSending;
    private Confirmation _confirmation;
    private string? _errorMessage;

    /// <param name="submit">Envía el reporte; el bool indica si el usuario dejó marcada la casilla de los registros.</param>
    public BugReportDialogViewModel(BugReportContext context, Func<BugReport, bool, Task<FeedbackSendOutcome>> submit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _submit = submit ?? throw new ArgumentNullException(nameof(submit));

        _category = Enum.IsDefined(context.Category) ? context.Category : BugCategory.Other;
        Categories = Enum.GetValues<BugCategory>()
            .Select(c => new BugCategoryOptionViewModel(c, c == _category, OnCategorySelected))
            .ToList();

        _nickname = (context.Nickname ?? "").Trim();
        if (_nickname.Length > SurveyDialogViewModel.NicknameMaxLength) _nickname = _nickname[..SurveyDialogViewModel.NicknameMaxLength];

        Intro = string.IsNullOrWhiteSpace(context.Intro) ? DefaultIntro : context.Intro.Trim();
        MetaText = BuildMetaText(context.GameVersionDisplay ?? context.GameVersion, context.LauncherVersion);
        Attachments = context.Attachments ?? Array.Empty<BugReportAttachmentPreview>();

        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        CancelCommand = new RelayCommand(Cancel, () => !IsSending);
        DiscardCommand = new RelayCommand(Discard, () => !IsSending);
        KeepEditingCommand = new RelayCommand(KeepEditing);
    }

    public string Title => "Reportar un problema";
    public string Intro { get; }

    /// <summary>"Coop2D 0.0.3 · Prototipo 2 · Launcher 1.0.5".</summary>
    public string MetaText { get; }

    public bool IsDanger => false;

    public IReadOnlyList<BugCategoryOptionViewModel> Categories { get; }

    public BugCategory SelectedCategory => _category;

    /// <summary>«¿Qué ha pasado?» (obligatorio).</summary>
    public string Description
    {
        get => _description;
        set
        {
            var text = value ?? "";
            if (text.Length > MaxDescriptionLength) text = text[..MaxDescriptionLength];
            if (!SetProperty(ref _description, text)) return;
            OnPropertyChanged(nameof(DescriptionCounterText));
            OnPropertyChanged(nameof(IsDescriptionNearLimit));
            OnPropertyChanged(nameof(ShowDescriptionPlaceholder));
            OnPropertyChanged(nameof(IsDescriptionValid));
            OnPropertyChanged(nameof(DescriptionHint));
            OnPropertyChanged(nameof(ShowDescriptionHint));
            OnTextChanged();
        }
    }

    /// <summary>«¿Cómo podemos reproducirlo?» (opcional).</summary>
    public string Steps
    {
        get => _steps;
        set
        {
            var text = value ?? "";
            if (text.Length > MaxStepsLength) text = text[..MaxStepsLength];
            if (!SetProperty(ref _steps, text)) return;
            OnPropertyChanged(nameof(StepsCounterText));
            OnPropertyChanged(nameof(IsStepsNearLimit));
            OnPropertyChanged(nameof(ShowStepsPlaceholder));
            OnTextChanged();
        }
    }

    public int DescriptionMaxLength => MaxDescriptionLength;
    public int StepsMaxLength => MaxStepsLength;
    public int NicknameMaxLength => SurveyDialogViewModel.NicknameMaxLength;

    public string DescriptionPlaceholder => "Por ejemplo: al entrar en la sala del jefe con 3 jugadores, el juego se queda congelado.";
    public string StepsPlaceholder => "Por ejemplo: 1. Crear partida  2. Unirse un amigo  3. Abrir la puerta del fondo";

    public bool ShowDescriptionPlaceholder => _description.Length == 0;
    public bool ShowStepsPlaceholder => _steps.Length == 0;

    /// <summary>"12 / 2000".</summary>
    public string DescriptionCounterText => Counter(_description.Length, MaxDescriptionLength);

    public string StepsCounterText => Counter(_steps.Length, MaxStepsLength);
    public bool IsDescriptionNearLimit => _description.Length >= MaxDescriptionLength * 0.9;
    public bool IsStepsNearLimit => _steps.Length >= MaxStepsLength * 0.9;

    /// <summary>Lo escrito llega al mínimo (sin contar espacios al principio y al final).</summary>
    public bool IsDescriptionValid => _description.Trim().Length >= MinDescriptionLength;

    /// <summary>"Escribe al menos 10 caracteres" / "Faltan 3 caracteres" / "" (ya vale).</summary>
    public string DescriptionHint
    {
        get
        {
            var length = _description.Trim().Length;
            if (length >= MinDescriptionLength) return "";
            if (length == 0) return $"Escribe al menos {MinDescriptionLength.ToString(CultureInfo.InvariantCulture)} caracteres";
            var missing = MinDescriptionLength - length;
            return missing == 1 ? "Falta 1 carácter" : $"Faltan {missing.ToString(CultureInfo.InvariantCulture)} caracteres";
        }
    }

    public bool ShowDescriptionHint => !_isSending && !IsDescriptionValid;

    /// <summary>Nombre o apodo (opcional; se recuerda para las opiniones y los reportes).</summary>
    public string Nickname
    {
        get => _nickname;
        set
        {
            var text = value ?? "";
            if (text.Length > SurveyDialogViewModel.NicknameMaxLength) text = text[..SurveyDialogViewModel.NicknameMaxLength];
            SetProperty(ref _nickname, text);
        }
    }

    /// <summary>«Adjuntar registros del juego y del launcher (recomendado)», marcada por defecto.</summary>
    public bool AttachLogs
    {
        get => _attachLogs;
        set
        {
            if (SetProperty(ref _attachLogs, value)) OnPropertyChanged(nameof(AttachmentsOpacity));
        }
    }

    /// <summary>La lista de adjuntos se atenúa si se desmarca la casilla.</summary>
    public double AttachmentsOpacity => _attachLogs ? 1.0 : 0.45;

    public IReadOnlyList<BugReportAttachmentPreview> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public string PrivacyNote => "Se ocultan tu nombre de usuario de Windows y el nombre del equipo.";

    public string FooterNote =>
        $"Se enviará al equipo de {LauncherConfig.GameDisplayName} junto con la versión del juego, del launcher y de Windows (y tu nombre si lo escribes).";

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (!SetProperty(ref _isSending, value)) return;
            OnPropertyChanged(nameof(IsInputEnabled));
            OnPropertyChanged(nameof(SendButtonText));
            OnPropertyChanged(nameof(CanSend));
            OnPropertyChanged(nameof(ShowDescriptionHint));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsInputEnabled => !_isSending;

    public string SendButtonText => _isSending ? "Enviando…" : "Enviar";

    public bool IsConfirmingDiscard => _confirmation == Confirmation.Discard;
    public bool IsConfirmingExit => _confirmation == Confirmation.Exit;
    public bool IsConfirming => _confirmation != Confirmation.None;
    public bool IsFooterButtonsVisible => !IsConfirming;
    public string ConfirmText => _confirmation == Confirmation.Exit ? ExitQuestion : DiscardQuestion;
    public string ConfirmActionText => _confirmation == Confirmation.Exit ? "Salir" : "Descartar";
    public string KeepEditingText => _confirmation == Confirmation.Exit ? "Volver" : "Seguir escribiendo";

    /// <summary>Ya hay algo escrito (Cancelar/Esc pide confirmación).</summary>
    public bool HasText => !string.IsNullOrWhiteSpace(_description) || !string.IsNullOrWhiteSpace(_steps);

    /// <summary>Hay texto que se perdería al cerrar el launcher (ni se está enviando ni el diálogo se cerró ya).</summary>
    public bool HasUnsentText => HasText && !_isSending && !_completion.Task.IsCompleted;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(_errorMessage);

    public bool CanSend => !_isSending && !IsConfirming && IsDescriptionValid;

    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand KeepEditingCommand { get; }

    public Task<BugReportDialogResult?> Completion => _completion.Task;

    /// <summary>Esc o Cancelar: con texto pide confirmar (Esc otra vez = seguir escribiendo). Mientras envía, nada.</summary>
    public void Cancel()
    {
        if (_isSending) return;
        if (IsConfirming)
        {
            KeepEditing();
            return;
        }
        if (HasText)
        {
            SetConfirmation(Confirmation.Discard);
            return;
        }
        Complete(null);
    }

    /// <summary>Enter no envía: hay cuadros de texto multilínea.</summary>
    public void Accept()
    {
    }

    /// <summary>
    /// Se va a cerrar el launcher: con texto sin enviar muestra «Tienes un reporte sin enviar. ¿Salir sin enviarlo?» y
    /// devuelve true con «Salir» (se descarta) o false con «Volver»/Esc. Sin nada que perder, true al momento.
    /// </summary>
    public Task<bool> ConfirmExitAsync()
    {
        if (!HasUnsentText) return Task.FromResult(true);
        if (_exitConfirmation is { } pending) return pending.Task;

        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitConfirmation = confirmation;
        SetConfirmation(Confirmation.Exit);
        return confirmation.Task;
    }

    /// <summary>Cierra el diálogo sin preguntar si no hay nada escrito. True si se cerró.</summary>
    internal bool CloseIfEmpty()
    {
        if (_isSending || HasText) return false;
        Complete(null);
        return true;
    }

    /// <summary>El reporte tal como se enviaría (textos recortados, categoría, versiones…). Mismo Id en cada reintento.</summary>
    internal BugReport BuildReport()
    {
        var steps = _steps.Trim();
        var nickname = _nickname.Trim();
        return new BugReport
        {
            Id = _reportId,
            Category = _category,
            Description = _description.Trim(),
            Steps = steps.Length > 0 ? steps : null,
            Nickname = nickname.Length > 0 ? nickname : null,
            GameVersion = string.IsNullOrWhiteSpace(_context.GameVersion) ? null : _context.GameVersion,
            GameVersionDisplay = string.IsNullOrWhiteSpace(_context.GameVersionDisplay) ? null : _context.GameVersionDisplay,
            LauncherVersion = _context.LauncherVersion ?? "",
            OsDescription = _context.OsDescription,
            ExitCode = _context.ExitCode,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private async Task SendAsync()
    {
        if (!CanSend) return;
        ErrorMessage = null;
        IsSending = true;
        try
        {
            var outcome = await _submit(BuildReport(), _attachLogs);
            Complete(new BugReportDialogResult(outcome));
        }
        catch (LauncherException ex)
        {
            Log.Warn($"No se pudo enviar el reporte: {ex.UserMessage}", ex);
            ErrorMessage = ex.UserMessage;
        }
        catch (OperationCanceledException)
        {
            Log.Info("Envío del reporte cancelado.");
            ErrorMessage = GenericSendError;
        }
        catch (Exception ex)
        {
            Log.Error("Error inesperado al enviar el reporte.", ex);
            ErrorMessage = GenericSendError;
        }
        finally
        {
            IsSending = false;
        }
    }

    private void OnCategorySelected(BugCategoryOptionViewModel option)
    {
        _category = option.Category;
        foreach (var other in Categories)
        {
            if (!ReferenceEquals(other, option)) other.IsSelected = false;
        }
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private void OnTextChanged()
    {
        OnPropertyChanged(nameof(HasText));
        OnPropertyChanged(nameof(CanSend));
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void Discard()
    {
        if (_isSending) return;
        Complete(null);
    }

    private void KeepEditing()
    {
        ResolveExitConfirmation(leave: false);
        SetConfirmation(Confirmation.None);
    }

    private void Complete(BugReportDialogResult? result)
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

    private static string Counter(int length, int max)
        => $"{length.ToString(CultureInfo.InvariantCulture)} / {max.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"Coop2D 0.0.3 · Prototipo 2 · Launcher 1.0.5" (sin juego instalado: "Coop2D no instalado · Launcher 1.0.5").</summary>
    internal static string BuildMetaText(string? gameVersion, string? launcherVersion)
    {
        var game = string.IsNullOrWhiteSpace(gameVersion)
            ? $"{LauncherConfig.GameDisplayName} no instalado"
            : $"{LauncherConfig.GameDisplayName} {gameVersion.Trim()}";
        return string.IsNullOrWhiteSpace(launcherVersion) ? game : $"{game} · Launcher {launcherVersion.Trim()}";
    }
}
