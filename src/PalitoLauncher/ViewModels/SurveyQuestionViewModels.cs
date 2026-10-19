using System.Globalization;
using PalitoLauncher.Core.Feedback;

namespace PalitoLauncher.ViewModels;

/// <summary>
/// Una pregunta de la encuesta en el diálogo. Hay una subclase por tipo (cada una con su plantilla en SurveyDialogView);
/// todas saben si están respondidas y construyen su <see cref="SurveyAnswer"/>.
/// </summary>
public abstract class SurveyQuestionViewModel : ObservableObject
{
    protected SurveyQuestionViewModel(SurveyQuestion question, int number)
    {
        Question = question ?? throw new ArgumentNullException(nameof(question));
        NumberText = number.ToString(CultureInfo.InvariantCulture) + ".";
    }

    public SurveyQuestion Question { get; }
    public string Id => Question.Id;

    /// <summary>"1.", "2."…</summary>
    public string NumberText { get; }

    public string Text => Question.Text ?? "";
    public string? Hint => string.IsNullOrWhiteSpace(Question.Hint) ? null : Question.Hint;
    public bool HasHint => Hint != null;
    public bool IsRequired => Question.Required;

    /// <summary>" *" si es obligatoria (se pinta en color de acento tras el texto).</summary>
    public string RequiredMarker => IsRequired ? " *" : "";

    /// <summary>Nombre accesible: el texto de la pregunta y, si lo es, "obligatoria".</summary>
    public string AutomationName => IsRequired ? $"{Text} (obligatoria)" : Text;

    /// <summary>Hay una respuesta válida (un texto solo con espacios no cuenta).</summary>
    public abstract bool IsAnswered { get; }

    /// <summary>La respuesta para enviar, o null si no está respondida.</summary>
    public abstract SurveyAnswer? BuildAnswer();

    /// <summary>Cambió la respuesta (para revalidar las obligatorias).</summary>
    internal event EventHandler? AnswerChanged;

    protected void NotifyAnswerChanged()
    {
        OnPropertyChanged(nameof(IsAnswered));
        AnswerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Crea el ViewModel según el tipo; null si el tipo no se conoce (la pregunta no se muestra).</summary>
    public static SurveyQuestionViewModel? Create(SurveyQuestion question, int number) => question?.Type switch
    {
        SurveyQuestionType.Rating => new RatingQuestionViewModel(question, number),
        SurveyQuestionType.Scale => new ScaleQuestionViewModel(question, number),
        SurveyQuestionType.Choice => new ChoiceQuestionViewModel(question, number),
        SurveyQuestionType.Multi => new MultiQuestionViewModel(question, number),
        SurveyQuestionType.Text => new TextQuestionViewModel(question, number),
        _ => null,
    };
}

/// <summary>Opción de una pregunta Scale/Choice/Multi (pill o botón de la escala).</summary>
public sealed class SurveyOptionViewModel : ObservableObject
{
    private readonly Action<SurveyOptionViewModel, bool> _onToggled;
    private bool _isSelected;

    internal SurveyOptionViewModel(string text, int value, string groupName, string automationName, bool isExclusive,
        Action<SurveyOptionViewModel, bool> onToggled)
    {
        Text = text;
        Value = value;
        GroupName = groupName;
        AutomationName = automationName;
        IsExclusive = isExclusive;
        _onToggled = onToggled;
    }

    public string Text { get; }

    /// <summary>Valor numérico (escala); en Choice/Multi es la posición.</summary>
    public int Value { get; }

    /// <summary>Grupo de los RadioButton (uno por pregunta).</summary>
    public string GroupName { get; }

    public string AutomationName { get; }

    /// <summary>«Ninguno»/«Ninguna» en una pregunta Multi: excluye a las demás.</summary>
    public bool IsExclusive { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            _onToggled(this, value);
        }
    }

    /// <summary>Cambia la selección sin avisar a la pregunta (lo usa la propia pregunta para aplicar sus reglas).</summary>
    internal void SetSelectedSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));
    }
}

/// <summary>Valoración con 5 estrellas. <see cref="Value"/> 0 = sin responder.</summary>
public sealed class RatingQuestionViewModel : SurveyQuestionViewModel
{
    private int _value;

    public RatingQuestionViewModel(SurveyQuestion question, int number) : base(question, number)
    {
    }

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 5);
            if (!SetProperty(ref _value, clamped)) return;
            NotifyAnswerChanged();
        }
    }

    public override bool IsAnswered => _value is >= 1 and <= 5;

    public override SurveyAnswer? BuildAnswer() => IsAnswered ? new SurveyAnswer { Number = _value } : null;
}

/// <summary>Escala numérica Min..Max (botones en fila) con etiquetas opcionales bajo los extremos.</summary>
public sealed class ScaleQuestionViewModel : SurveyQuestionViewModel
{
    public ScaleQuestionViewModel(SurveyQuestion question, int number) : base(question, number)
    {
        // Ya viene saneada (0..10, Min < Max), pero se protege igualmente para no crear cientos de botones.
        int min = Math.Clamp(question.Min, 0, 10);
        int max = Math.Clamp(question.Max, 0, 10);
        if (max <= min) (min, max) = (1, 5);

        var group = "scale-" + Guid.NewGuid().ToString("N");
        var options = new List<SurveyOptionViewModel>();
        for (var v = min; v <= max; v++)
        {
            var label = v == min ? question.MinLabel : v == max ? question.MaxLabel : null;
            var text = v.ToString(CultureInfo.InvariantCulture);
            var automation = string.IsNullOrWhiteSpace(label) ? $"{text} de {max}" : $"{text} de {max}: {label}";
            options.Add(new SurveyOptionViewModel(text, v, group, automation, isExclusive: false, OnToggled));
        }
        Options = options;
    }

    public IReadOnlyList<SurveyOptionViewModel> Options { get; }
    public string? MinLabel => string.IsNullOrWhiteSpace(Question.MinLabel) ? null : Question.MinLabel;
    public string? MaxLabel => string.IsNullOrWhiteSpace(Question.MaxLabel) ? null : Question.MaxLabel;
    public bool HasLabels => MinLabel != null || MaxLabel != null;

    /// <summary>Valor elegido o null.</summary>
    public int? Value => Options.FirstOrDefault(o => o.IsSelected)?.Value;

    public override bool IsAnswered => Value != null;

    public override SurveyAnswer? BuildAnswer() => Value is int v ? new SurveyAnswer { Number = v } : null;

    private void OnToggled(SurveyOptionViewModel option, bool selected)
    {
        if (selected)
        {
            foreach (var other in Options)
                if (!ReferenceEquals(other, option)) other.SetSelectedSilently(false);
        }
        OnPropertyChanged(nameof(Value));
        NotifyAnswerChanged();
    }
}

/// <summary>Una opción de la lista (pills de selección única).</summary>
public sealed class ChoiceQuestionViewModel : SurveyQuestionViewModel
{
    public ChoiceQuestionViewModel(SurveyQuestion question, int number) : base(question, number)
    {
        var group = "choice-" + Guid.NewGuid().ToString("N");
        Options = (question.Options ?? new List<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select((o, i) => new SurveyOptionViewModel(o, i, group, o, isExclusive: false, OnToggled))
            .ToList();
    }

    public IReadOnlyList<SurveyOptionViewModel> Options { get; }

    /// <summary>Opción elegida o null.</summary>
    public string? Selected => Options.FirstOrDefault(o => o.IsSelected)?.Text;

    public override bool IsAnswered => Selected != null;

    public override SurveyAnswer? BuildAnswer()
        => Selected is { } s ? new SurveyAnswer { Choices = new List<string> { s } } : null;

    private void OnToggled(SurveyOptionViewModel option, bool selected)
    {
        if (selected)
        {
            foreach (var other in Options)
                if (!ReferenceEquals(other, option)) other.SetSelectedSilently(false);
        }
        OnPropertyChanged(nameof(Selected));
        NotifyAnswerChanged();
    }
}

/// <summary>
/// Varias opciones (pills con selección múltiple). Una opción «Ninguno»/«Ninguna» es excluyente: al elegirla se quitan
/// las demás, y al elegir otra se quita ella.
/// </summary>
public sealed class MultiQuestionViewModel : SurveyQuestionViewModel
{
    public MultiQuestionViewModel(SurveyQuestion question, int number) : base(question, number)
    {
        var group = "multi-" + Guid.NewGuid().ToString("N");
        Options = (question.Options ?? new List<string>())
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select((o, i) => new SurveyOptionViewModel(o, i, group, o, IsNoneOption(o), OnToggled))
            .ToList();
    }

    public IReadOnlyList<SurveyOptionViewModel> Options { get; }

    /// <summary>Opciones elegidas, en el orden de la encuesta.</summary>
    public IReadOnlyList<string> Selected => Options.Where(o => o.IsSelected).Select(o => o.Text).ToList();

    public override bool IsAnswered => Options.Any(o => o.IsSelected);

    public override SurveyAnswer? BuildAnswer()
        => IsAnswered ? new SurveyAnswer { Choices = Selected.ToList() } : null;

    /// <summary>¿Es la opción "ninguno" (excluyente)? Sin distinguir mayúsculas ni espacios/puntuación final.</summary>
    internal static bool IsNoneOption(string? option)
    {
        var text = (option ?? "").Trim().TrimEnd('.', '!', ' ').Trim();
        return string.Equals(text, "Ninguno", StringComparison.OrdinalIgnoreCase)
               || string.Equals(text, "Ninguna", StringComparison.OrdinalIgnoreCase);
    }

    private void OnToggled(SurveyOptionViewModel option, bool selected)
    {
        if (selected)
        {
            foreach (var other in Options)
            {
                if (ReferenceEquals(other, option)) continue;
                // Elegir «Ninguno» quita todas las demás; elegir otra quita «Ninguno».
                if (option.IsExclusive || other.IsExclusive) other.SetSelectedSilently(false);
            }
        }
        OnPropertyChanged(nameof(Selected));
        NotifyAnswerChanged();
    }
}

/// <summary>Texto libre (multilínea) con contador de caracteres.</summary>
public sealed class TextQuestionViewModel : SurveyQuestionViewModel
{
    private string _answerText = "";

    public TextQuestionViewModel(SurveyQuestion question, int number) : base(question, number)
    {
        MaxLength = Math.Clamp(question.MaxLength <= 0 ? 1000 : question.MaxLength, 1, 1000);
    }

    public int MaxLength { get; }
    public string? Placeholder => string.IsNullOrWhiteSpace(Question.Placeholder) ? null : Question.Placeholder;

    /// <summary>Ayuda accesible del cuadro de texto (nunca null: AutomationProperties no admite null).</summary>
    public string HelpText => Placeholder ?? "";

    public string AnswerText
    {
        get => _answerText;
        set
        {
            var text = value ?? "";
            if (text.Length > MaxLength) text = text[..MaxLength];
            bool wasAnswered = IsAnswered;
            if (!SetProperty(ref _answerText, text)) return;
            OnPropertyChanged(nameof(CounterText));
            OnPropertyChanged(nameof(ShowPlaceholder));
            OnPropertyChanged(nameof(IsNearLimit));
            if (wasAnswered != IsAnswered) NotifyAnswerChanged();
        }
    }

    /// <summary>Se muestra el texto de ejemplo mientras no hay nada escrito.</summary>
    public bool ShowPlaceholder => Placeholder != null && _answerText.Length == 0;

    /// <summary>"123 / 1000".</summary>
    public string CounterText => $"{_answerText.Length.ToString(CultureInfo.InvariantCulture)} / {MaxLength.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Quedan menos del 10 % de caracteres (el contador se pinta en ámbar).</summary>
    public bool IsNearLimit => _answerText.Length >= MaxLength * 0.9;

    public override bool IsAnswered => !string.IsNullOrWhiteSpace(_answerText);

    public override SurveyAnswer? BuildAnswer() => IsAnswered ? new SurveyAnswer { Text = _answerText.Trim() } : null;
}
