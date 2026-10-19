using System.Windows.Input;

namespace PalitoLauncher.ViewModels;

/// <summary>
/// Tarjeta «Próxima sesión de prueba» de Inicio. Una sola instancia: el MainViewModel le pone los datos (al leer
/// launcher.json, cada 30 s para la cuenta atrás y cuando cambian la instalación o la lista de versiones).
/// </summary>
public sealed class PlaytestCardViewModel : ObservableObject
{
    private bool _isVisible;
    private string? _id;
    private string _headerText = "";
    private string _title = "";
    private string _whenText = "";
    private string _countdownText = "";
    private bool _isOngoing;
    private bool _isSoon;
    private string? _description;
    private bool _hasVersionInfo;
    private bool _isVersionOk;
    private string _versionText = "";
    private string? _versionActionText;
    private string? _versionHint;
    private bool _hasDiscord;

    internal PlaytestCardViewModel(Action addToCalendar, Action joinDiscord, Action versionAction, Func<bool> canRunVersionAction)
    {
        ArgumentNullException.ThrowIfNull(addToCalendar);
        ArgumentNullException.ThrowIfNull(joinDiscord);
        ArgumentNullException.ThrowIfNull(versionAction);
        ArgumentNullException.ThrowIfNull(canRunVersionAction);

        AddToCalendarCommand = new RelayCommand(addToCalendar, () => IsVisible);
        JoinDiscordCommand = new RelayCommand(joinDiscord, () => IsVisible && HasDiscord);
        VersionActionCommand = new RelayCommand(versionAction, () => IsVisible && HasVersionAction && canRunVersionAction());
    }

    /// <summary>Hay una sesión en curso o en los próximos 30 días.</summary>
    public bool IsVisible { get => _isVisible; internal set => SetProperty(ref _isVisible, value); }

    /// <summary>Id de la sesión mostrada (launcher.json).</summary>
    internal string? Id { get => _id; set => _id = value; }

    /// <summary>"Próxima sesión de prueba" / "Sesión de prueba en curso".</summary>
    public string HeaderText { get => _headerText; internal set => SetProperty(ref _headerText, value); }

    public string Title { get => _title; internal set => SetProperty(ref _title, value); }

    /// <summary>"sábado 24 oct · 21:00 – 23:00" (hora local).</summary>
    public string WhenText { get => _whenText; internal set => SetProperty(ref _whenText, value); }

    /// <summary>"Empieza en 3 h 12 min" / "En curso · termina a las 23:00".</summary>
    public string CountdownText { get => _countdownText; internal set => SetProperty(ref _countdownText, value); }

    public bool IsOngoing { get => _isOngoing; internal set => SetProperty(ref _isOngoing, value); }

    /// <summary>En curso o a punto de empezar (dentro del aviso): la tarjeta se resalta con el color de acento.</summary>
    public bool IsSoon { get => _isSoon; internal set => SetProperty(ref _isSoon, value); }

    public string? Description
    {
        get => _description;
        internal set
        {
            if (SetProperty(ref _description, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
                OnPropertyChanged(nameof(HasDescription));
        }
    }

    public bool HasDescription => _description != null;

    /// <summary>La sesión pide una versión concreta.</summary>
    public bool HasVersionInfo { get => _hasVersionInfo; internal set => SetProperty(ref _hasVersionInfo, value); }

    /// <summary>Tiene instalada la versión que pide la sesión (✓ verde; si no, aviso ámbar).</summary>
    public bool IsVersionOk { get => _isVersionOk; internal set => SetProperty(ref _isVersionOk, value); }

    /// <summary>"Tienes la versión correcta" / "Necesitas 0.0.3 · Prototipo 2".</summary>
    public string VersionText { get => _versionText; internal set => SetProperty(ref _versionText, value); }

    /// <summary>"Actualizar" / "Instalar esa versión", o null si no hay nada que hacer (o no se encuentra esa versión).</summary>
    public string? VersionActionText
    {
        get => _versionActionText;
        internal set
        {
            if (SetProperty(ref _versionActionText, string.IsNullOrWhiteSpace(value) ? null : value))
                OnPropertyChanged(nameof(HasVersionAction));
        }
    }

    public bool HasVersionAction => _versionActionText != null;

    /// <summary>Ayuda cuando la versión pedida no aparece en la lista (p.ej. es de prueba y no están activadas).</summary>
    public string? VersionHint
    {
        get => _versionHint;
        internal set
        {
            if (SetProperty(ref _versionHint, string.IsNullOrWhiteSpace(value) ? null : value))
                OnPropertyChanged(nameof(HasVersionHint));
        }
    }

    public bool HasVersionHint => _versionHint != null;

    public bool HasDiscord { get => _hasDiscord; internal set => SetProperty(ref _hasDiscord, value); }

    /// <summary>Guarda un .ics («Coop2D - título.ics») para añadir la sesión al calendario.</summary>
    public ICommand AddToCalendarCommand { get; }

    public ICommand JoinDiscordCommand { get; }

    /// <summary>«Actualizar» / «Instalar esa versión».</summary>
    public ICommand VersionActionCommand { get; }

    /// <summary>Sin sesión que mostrar.</summary>
    internal void Clear()
    {
        IsVisible = false;
        Id = null;
        IsOngoing = false;
        IsSoon = false;
        HasVersionInfo = false;
        VersionActionText = null;
        VersionHint = null;
        HasDiscord = false;
    }
}
