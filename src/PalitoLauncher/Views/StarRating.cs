using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PalitoLauncher.Views;

/// <summary>
/// Valoración con 5 estrellas (ToggleButtons con los glifos E734 vacía / E735 llena). Clic para elegir, vista previa al
/// pasar el ratón y teclado: ←/→ bajan/suben, Inicio/Fin = 1/5 y las teclas 1-5 eligen directamente. Un solo punto de
/// tabulación (la estrella elegida, o la primera). <see cref="Value"/> 0 = sin valorar. <see cref="RatedCommand"/> se
/// ejecuta con el valor (int) cada vez que el usuario elige. Con <see cref="CommitOnNavigation"/> = false (la invitación
/// tras la partida, donde elegir abre la encuesta) las teclas solo mueven el foco y la vista previa: se elige con clic,
/// Enter o Espacio sobre una estrella.
/// </summary>
public sealed class StarRating : UserControl
{
    public const int MaxStars = 5;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(StarRating),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVisualPropertyChanged, CoerceValue));

    public static readonly DependencyProperty StarSizeProperty = DependencyProperty.Register(
        nameof(StarSize), typeof(double), typeof(StarRating), new PropertyMetadata(26.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty ShowCaptionProperty = DependencyProperty.Register(
        nameof(ShowCaption), typeof(bool), typeof(StarRating), new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty RatedCommandProperty = DependencyProperty.Register(
        nameof(RatedCommand), typeof(ICommand), typeof(StarRating), new PropertyMetadata(null));

    public static readonly DependencyProperty CommitOnNavigationProperty = DependencyProperty.Register(
        nameof(CommitOnNavigation), typeof(bool), typeof(StarRating), new PropertyMetadata(true, OnVisualPropertyChanged));

    private readonly StarButton[] _stars = new StarButton[MaxStars];
    private readonly TextBlock _caption;
    private int _hover;

    public StarRating()
    {
        Focusable = false;
        IsTabStop = false;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var starsPanel = new StackPanel { Orientation = Orientation.Horizontal, Background = System.Windows.Media.Brushes.Transparent };
        starsPanel.MouseLeave += (_, _) => SetHover(0);

        for (var i = 0; i < MaxStars; i++)
        {
            var index = i + 1;
            // Se elige en OnToggle: lo llaman tanto el clic (y Enter/Espacio) como los lectores de pantalla y UI Automation
            // (TogglePattern), que no lanzan el evento Click.
            var star = new StarButton { IsThreeState = false, Activated = () => Pick(index, focus: false) };
            star.SetResourceReference(StyleProperty, "StarToggle");
            AutomationProperties.SetName(star, index == 1 ? "1 estrella" : $"{index} estrellas");
            AutomationProperties.SetHelpText(star, Caption(index));
            star.ToolTip = Caption(index);
            star.MouseEnter += (_, _) => SetHover(index);
            // Sin elegir con las teclas, la estrella con el foco se previsualiza (se ve qué se elegiría con Enter).
            star.GotKeyboardFocus += (_, _) =>
            {
                if (!CommitOnNavigation) SetHover(index);
            };
            _stars[i] = star;
            starsPanel.Children.Add(star);
        }

        _caption = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextDimBrush");

        panel.Children.Add(starsPanel);
        panel.Children.Add(_caption);
        Content = panel;

        PreviewKeyDown += OnPreviewKeyDown;
        // Al ocultarse (p.ej. la invitación tras pulsar una estrella) no debe quedar la vista previa del ratón.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) SetHover(0);
        };
        // Ni la del teclado al salir de las estrellas (con el ratón encima se mantiene la suya).
        IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if (e.NewValue is false && !IsMouseOver) SetHover(0);
        };
        Refresh();
    }

    /// <summary>Valoración elegida (1..5) o 0 si no hay ninguna.</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Tamaño del glifo de cada estrella.</summary>
    public double StarSize
    {
        get => (double)GetValue(StarSizeProperty);
        set => SetValue(StarSizeProperty, value);
    }

    /// <summary>Muestra al lado el texto de la valoración ("Bien", "¡Genial!"…), también durante la vista previa.</summary>
    public bool ShowCaption
    {
        get => (bool)GetValue(ShowCaptionProperty);
        set => SetValue(ShowCaptionProperty, value);
    }

    /// <summary>
    /// Se ejecuta con el valor elegido (int) cada vez que el usuario elige: clic, Enter/Espacio sobre una estrella o, con
    /// <see cref="CommitOnNavigation"/>, las teclas de navegación.
    /// </summary>
    public ICommand? RatedCommand
    {
        get => (ICommand?)GetValue(RatedCommandProperty);
        set => SetValue(RatedCommandProperty, value);
    }

    /// <summary>
    /// True (por defecto): ←/→, Inicio/Fin y 1-5 eligen al momento. False: solo mueven el foco y la vista previa, y se
    /// elige con clic, Enter o Espacio (para cuando elegir hace algo más, como abrir la encuesta).
    /// </summary>
    public bool CommitOnNavigation
    {
        get => (bool)GetValue(CommitOnNavigationProperty);
        set => SetValue(CommitOnNavigationProperty, value);
    }

    /// <summary>Texto corto de cada valoración (tooltip, ayuda accesible y leyenda).</summary>
    public static string Caption(int value) => value switch
    {
        1 => "Muy mal",
        2 => "Mal",
        3 => "Normal",
        4 => "Bien",
        5 => "¡Genial!",
        _ => "",
    };

    private static object CoerceValue(DependencyObject d, object baseValue)
        => baseValue is int v ? Math.Clamp(v, 0, MaxStars) : 0;

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((StarRating)d).Refresh();

    private void Pick(int value, bool focus)
    {
        value = Math.Clamp(value, 1, MaxStars);
        SetCurrentValue(ValueProperty, value); // conserva el binding (y lo actualiza si es TwoWay)
        Refresh(); // IsChecked siempre refleja el valor (StarButton no lo cambia por su cuenta)
        if (focus) _stars[value - 1].Focus();

        var command = RatedCommand;
        if (command != null && command.CanExecute(value)) command.Execute(value);
    }

    private void SetHover(int value)
    {
        if (_hover == value) return;
        _hover = value;
        Refresh();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key)) e.Handled = true;
    }

    /// <summary>
    /// Teclas de navegación (←/→, Inicio/Fin, 1-5). Eligen al momento, o con <see cref="CommitOnNavigation"/> = false solo
    /// llevan el foco y la vista previa a esa estrella (Enter/Espacio no pasan por aquí: los atiende la propia estrella como
    /// un clic). True si la tecla era de navegación.
    /// </summary>
    internal bool HandleKey(Key key)
    {
        if (!IsEnabled) return false;
        if (CommitOnNavigation)
        {
            if (KeyTarget(key, Value) is not int value) return false;
            Pick(value, focus: true);
            return true;
        }

        if (KeyTarget(key, NavigationPosition()) is not int target) return false;
        SetHover(target);
        _stars[target - 1].Focus();
        return true;
    }

    /// <summary>Estrella desde la que se mueve el teclado sin elegir: la que tiene el foco, la previsualizada o la elegida.</summary>
    private int NavigationPosition()
    {
        for (var i = 0; i < MaxStars; i++)
            if (_stars[i].IsKeyboardFocused) return i + 1;
        return _hover > 0 ? _hover : Value;
    }

    /// <summary>Valoración a la que lleva una tecla (←/→, Inicio/Fin, 1-5) desde <paramref name="current"/>; null si no aplica.</summary>
    internal static int? KeyTarget(Key key, int current) => key switch
    {
        Key.Left => Math.Max(1, current - 1),
        Key.Right => Math.Min(MaxStars, current + 1),
        Key.Home => 1,
        Key.End => MaxStars,
        >= Key.D1 and <= Key.D5 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad5 => key - Key.NumPad1 + 1,
        _ => null,
    };

    private void Refresh()
    {
        int value = Value;
        int display = _hover > 0 ? _hover : value;
        int tabStop = Math.Max(1, value);
        for (var i = 0; i < MaxStars; i++)
        {
            var star = _stars[i];
            int index = i + 1;
            star.FontSize = StarSize;
            star.Tag = index <= display;          // iluminada (glifo lleno)
            star.IsChecked = index == value;      // para lectores de pantalla: la estrella elegida está "activada"
            star.IsTabStop = index == tabStop;    // un solo punto de tabulación; las flechas mueven la selección
        }

        _caption.Visibility = ShowCaption ? Visibility.Visible : Visibility.Collapsed;
        _caption.Text = Caption(display);
    }

    /// <summary>
    /// Estrella: en vez de alternar IsChecked por su cuenta, avisa a <see cref="Activated"/>. OnToggle se llama al hacer clic
    /// (ratón, Enter, Espacio) y también desde UI Automation (Narrador y otros lectores de pantalla), así todos eligen igual.
    /// </summary>
    internal sealed class StarButton : ToggleButton
    {
        public Action? Activated { get; init; }

        protected override void OnToggle() => Activated?.Invoke();
    }
}
