using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using PalitoLauncher.Core.Missions;

namespace PalitoLauncher.ViewModels;

/// <summary>Lo que el jugador puede hacer con una misión desde la tarjeta.</summary>
public enum MissionUserAction
{
    /// <summary>«Funcionó» (solo manuales).</summary>
    Worked,

    /// <summary>«Falló…» con una nota opcional.</summary>
    Failed,

    /// <summary>«No la probé».</summary>
    Skip,

    /// <summary>Vuelve a pendiente (deshace una marca a mano).</summary>
    Undo,

    /// <summary>Pregunta de comprobación: «Sí».</summary>
    CheckYes,

    /// <summary>Pregunta de comprobación: «No…» con una nota opcional.</summary>
    CheckNo,
}

/// <summary>Una misión en la tarjeta «Misiones de prueba».</summary>
public sealed class MissionItemViewModel : ObservableObject
{
    private readonly Action<string, MissionUserAction, string?> _act;
    private MissionUserAction _pendingNoteAction = MissionUserAction.Failed;
    private MissionState _state;
    private bool _isManual;
    private bool _automatic;
    private string _statusText = "";
    private string? _note;
    private string? _check;
    private bool? _checkAnswer;
    private bool _isEditingNote;
    private string _noteDraft = "";
    private string _noteLabel = "";

    internal MissionItemViewModel(string id, string title, string? hint, Action<string, MissionUserAction, string?> act)
    {
        Id = id;
        Title = title;
        Hint = hint;
        _act = act;

        WorkedCommand = new RelayCommand(() => _act(Id, MissionUserAction.Worked, null), () => ShowWorked);
        FailedCommand = new RelayCommand(() => BeginNote(MissionUserAction.Failed), () => ShowFailed);
        SkipCommand = new RelayCommand(() => _act(Id, MissionUserAction.Skip, null), () => ShowSkip);
        UndoCommand = new RelayCommand(() => _act(Id, MissionUserAction.Undo, null), () => ShowUndo);
        CheckYesCommand = new RelayCommand(() => _act(Id, MissionUserAction.CheckYes, null), () => ShowCheck);
        CheckNoCommand = new RelayCommand(() => BeginNote(MissionUserAction.CheckNo), () => ShowCheck);
        SaveNoteCommand = new RelayCommand(SaveNote, () => IsEditingNote);
        CancelNoteCommand = new RelayCommand(() => IsEditingNote = false, () => IsEditingNote);
    }

    public string Id { get; }
    public string Title { get; }
    public string? Hint { get; }
    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);

    /// <summary>La pista solo se muestra mientras no está completada.</summary>
    public bool ShowHint => HasHint && State != MissionState.Done;

    public MissionState State { get => _state; private set => SetProperty(ref _state, value); }

    public bool IsDone => State == MissionState.Done;
    public bool IsFailed => State == MissionState.Failed;
    public bool IsSkipped => State == MissionState.Skipped;
    public bool IsPending => State == MissionState.Pending;
    public bool IsManual => _isManual;

    /// <summary>Glifo (Segoe Fluent Icons): ✓ hecha, ✗ falló, — no probada, ○ pendiente.</summary>
    public string Glyph => State switch
    {
        MissionState.Done => "",
        MissionState.Failed => "",
        MissionState.Skipped => "",
        _ => "",
    };

    /// <summary>"Completada · 31 oct 21:14", "12 / 30", "Márcala tú", "Falló", "No la probaste".</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string? Note { get => _note; private set => SetProperty(ref _note, value); }
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    /// <summary>Pregunta de comprobación (solo se muestra en las completadas aún sin responder).</summary>
    public string? Check { get => _check; private set => SetProperty(ref _check, value); }

    public bool ShowWorked => _isManual && State is MissionState.Pending or MissionState.Skipped;
    public bool ShowFailed => State is MissionState.Pending or MissionState.Skipped;
    public bool ShowSkip => State == MissionState.Pending;
    public bool ShowUndo => State is MissionState.Failed or MissionState.Skipped || (State == MissionState.Done && !_automatic);
    public bool ShowCheck => State == MissionState.Done && _automatic && !string.IsNullOrWhiteSpace(Check) && _checkAnswer is null;
    public bool HasActions => !IsEditingNote && (ShowWorked || ShowFailed || ShowSkip || ShowUndo);

    public ICommand WorkedCommand { get; }
    public ICommand FailedCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand CheckYesCommand { get; }
    public ICommand CheckNoCommand { get; }
    public ICommand SaveNoteCommand { get; }
    public ICommand CancelNoteCommand { get; }

    // ───────────── Nota ─────────────

    public bool IsEditingNote
    {
        get => _isEditingNote;
        set
        {
            if (!SetProperty(ref _isEditingNote, value)) return;
            OnPropertyChanged(nameof(HasActions));
            OnPropertyChanged(nameof(ShowCheckRow));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool ShowCheckRow => ShowCheck && !IsEditingNote;

    public string NoteDraft
    {
        get => _noteDraft;
        set
        {
            var text = value ?? "";
            if (text.Length > MissionNotes.MaxNoteLength) text = text[..MissionNotes.MaxNoteLength];
            SetProperty(ref _noteDraft, text);
        }
    }

    public int NoteMaxLength => MissionNotes.MaxNoteLength;

    /// <summary>"¿Qué falló? (opcional)" / "¿Qué se vio mal? (opcional)".</summary>
    public string NoteLabel { get => _noteLabel; private set => SetProperty(ref _noteLabel, value); }

    private void BeginNote(MissionUserAction action)
    {
        _pendingNoteAction = action;
        NoteLabel = action == MissionUserAction.CheckNo ? "¿Qué se vio mal? (opcional)" : "¿Qué falló? (opcional)";
        NoteDraft = Note ?? "";
        IsEditingNote = true;
    }

    private void SaveNote()
    {
        var note = string.IsNullOrWhiteSpace(NoteDraft) ? null : NoteDraft.Trim();
        IsEditingNote = false;
        _act(Id, _pendingNoteAction, note);
    }

    /// <summary>Pone los datos del estado guardado.</summary>
    internal void Update(Mission mission, MissionProgress? progress)
    {
        _isManual = mission.Manual;
        _automatic = progress?.Automatic ?? false;
        _checkAnswer = progress?.CheckAnswer;
        Check = mission.Check;
        Note = progress?.Note;
        State = progress?.State ?? MissionState.Pending;
        StatusText = BuildStatus(mission, progress);

        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(ShowHint));
        OnPropertyChanged(nameof(ShowWorked));
        OnPropertyChanged(nameof(ShowFailed));
        OnPropertyChanged(nameof(ShowSkip));
        OnPropertyChanged(nameof(ShowUndo));
        OnPropertyChanged(nameof(ShowCheck));
        OnPropertyChanged(nameof(ShowCheckRow));
        OnPropertyChanged(nameof(HasActions));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string BuildStatus(Mission mission, MissionProgress? p)
    {
        var culture = CultureInfo.GetCultureInfo("es-ES");
        switch (p?.State ?? MissionState.Pending)
        {
            case MissionState.Done:
                var whenText = p!.CompletedUtc is { } at ? " · " + at.ToLocalTime().ToString("d MMM HH:mm", culture) : "";
                var extra = p.WithCheats ? " · con trucos" : "";
                var check = p.CheckAnswer switch { true => " · se vio bien", false => " · se vio mal", _ => "" };
                return (p.Automatic ? "Completada" : "Funcionó") + whenText + extra + check;
            case MissionState.Failed:
                return "Falló";
            case MissionState.Skipped:
                return "No la probaste";
            default:
                if (mission.Manual) return "Márcala tú al terminar de probarla";
                return MissionEvaluator.ProgressText(mission, p) is { } progress ? $"Progreso: {progress}" : "Se marca sola al conseguirla";
        }
    }
}

/// <summary>Tarjeta «Misiones de prueba» de Inicio. Una sola instancia: el MainViewModel le pone los datos.</summary>
public sealed class MissionsCardViewModel : ObservableObject
{
    private readonly Action<string, MissionUserAction, string?> _act;
    private bool _isVisible;
    private string _title = "";
    private string? _intro;
    private string _progressText = "";
    private double _progressValue;
    private string? _versionWarning;
    private string? _setId;

    internal MissionsCardViewModel(Action<string, MissionUserAction, string?> act, Action sendNow, Func<bool> canSendNow)
    {
        _act = act ?? throw new ArgumentNullException(nameof(act));
        ArgumentNullException.ThrowIfNull(sendNow);
        ArgumentNullException.ThrowIfNull(canSendNow);
        SendNowCommand = new RelayCommand(sendNow, () => IsVisible && canSendNow());
    }

    public bool IsVisible { get => _isVisible; internal set => SetProperty(ref _isVisible, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string? Intro { get => _intro; private set => SetProperty(ref _intro, value); }
    public bool HasIntro => !string.IsNullOrWhiteSpace(Intro);

    /// <summary>"3 de 6 completadas".</summary>
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    /// <summary>0..100 para la barra.</summary>
    public double ProgressValue { get => _progressValue; private set => SetProperty(ref _progressValue, value); }

    /// <summary>Aviso si la versión instalada es anterior a la que necesitan las misiones.</summary>
    public string? VersionWarning { get => _versionWarning; internal set { if (SetProperty(ref _versionWarning, value)) OnPropertyChanged(nameof(HasVersionWarning)); } }
    public bool HasVersionWarning => !string.IsNullOrWhiteSpace(VersionWarning);

    public ObservableCollection<MissionItemViewModel> Items { get; } = new();

    /// <summary>«Enviar resultados ahora».</summary>
    public ICommand SendNowCommand { get; }

    internal void Clear()
    {
        IsVisible = false;
        _setId = null;
        Items.Clear();
        VersionWarning = null;
    }

    /// <summary>Rellena la tarjeta. Si el conjunto no cambió, actualiza las misiones en su sitio (no se pierde una nota a medias).</summary>
    internal void Apply(MissionSet set, MissionSetState state)
    {
        Title = set.Title;
        Intro = set.Intro;
        OnPropertyChanged(nameof(HasIntro));

        bool sameMissions = string.Equals(_setId, set.Id, StringComparison.Ordinal)
                            && Items.Count == set.Missions.Count
                            && Items.Select(i => (i.Id, i.Title, i.Hint)).SequenceEqual(set.Missions.Select(m => (m.Id, m.Title, m.Hint)));
        if (!sameMissions)
        {
            Items.Clear();
            foreach (var mission in set.Missions) Items.Add(new MissionItemViewModel(mission.Id, mission.Title, mission.Hint, _act));
            _setId = set.Id;
        }

        for (var i = 0; i < set.Missions.Count; i++)
        {
            var mission = set.Missions[i];
            state.Missions.TryGetValue(mission.Id, out var progress);
            Items[i].Update(mission, progress);
        }

        var done = Items.Count(i => i.IsDone);
        ProgressText = $"{done} de {Items.Count} completadas";
        ProgressValue = Items.Count == 0 ? 0 : 100.0 * done / Items.Count;
    }
}
