using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;
using PalitoLauncher.Views;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Control de 5 estrellas: clic (valor + comando), vista previa al pasar el ratón, teclado, un solo punto de tabulación,
/// binding TwoWay con la pregunta de valoración y accesibilidad.
/// </summary>
public sealed class StarRatingTests
{
    private static ToggleButton[] Stars(StarRating rating)
    {
        rating.Measure(new Size(400, 100));
        rating.Arrange(new Rect(0, 0, 400, 100));
        return Descendants(rating).OfType<ToggleButton>().ToArray();
    }

    private static bool[] Lit(IEnumerable<ToggleButton> stars) => stars.Select(s => s.Tag is true).ToArray();

    // Clic real: ButtonBase.OnClick (lo mismo que hace WPF con ratón, Enter o Espacio), que llama a OnToggle y lanza Click.
    private static readonly System.Reflection.MethodInfo OnClickMethod = typeof(ButtonBase).GetMethod("OnClick",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static void Click(ToggleButton star) => OnClickMethod.Invoke(star, null);

    // Lo que hacen Narrador y otros lectores de pantalla (y UI Automation): TogglePattern.Toggle, sin evento Click.
    private static void ToggleViaAutomation(ToggleButton star)
    {
        var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(star);
        var toggle = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(
            System.Windows.Automation.Peers.PatternInterface.Toggle);
        toggle.Toggle();
    }

    // Regresión 1.0.3: con TogglePattern (lectores de pantalla) la estrella cambiaba de aspecto pero no se elegía.
    [Fact]
    public Task Automation_Toggle_PicksTheStar_AndRunsTheCommandOnce() => WpfHost.RunAsync(() =>
    {
        var picked = new List<object?>();
        var rating = new StarRating { RatedCommand = new RelayCommand(p => picked.Add(p)) };
        var stars = Stars(rating);

        ToggleViaAutomation(stars[3]);

        Assert.Equal(4, rating.Value);
        Assert.Equal(new bool?[] { false, false, false, true, false }, stars.Select(s => s.IsChecked));
        Assert.Equal(new object?[] { 4 }, picked);

        // Un clic real tampoco ejecuta el comando dos veces (OnToggle + evento Click).
        Click(stars[1]);
        Assert.Equal(2, rating.Value);
        Assert.Equal(new object?[] { 4, 2 }, picked);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Click_SetsTheValue_LightsTheStars_AndRunsTheCommand() => WpfHost.RunAsync(() =>
    {
        var picked = new List<object?>();
        var rating = new StarRating { RatedCommand = new RelayCommand(p => picked.Add(p)) };
        var stars = Stars(rating);
        Assert.Equal(5, stars.Length);
        Assert.Equal(new[] { false, false, false, false, false }, Lit(stars));

        Click(stars[2]);

        Assert.Equal(3, rating.Value);
        Assert.Equal(new[] { true, true, true, false, false }, Lit(stars));
        Assert.Equal(new bool?[] { false, false, true, false, false }, stars.Select(s => s.IsChecked));
        Assert.Equal(new object?[] { 3 }, picked);

        // Pulsar la misma estrella otra vez no la desmarca (no es un interruptor).
        Click(stars[2]);
        Assert.Equal(3, rating.Value);
        Assert.True(stars[2].IsChecked);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Hover_PreviewsWithoutChangingTheValue() => WpfHost.RunAsync(() =>
    {
        var rating = new StarRating { Value = 2, ShowCaption = true };
        var stars = Stars(rating);

        stars[4].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        Assert.Equal(new[] { true, true, true, true, true }, Lit(stars));
        Assert.Equal(2, rating.Value);
        Assert.Contains(Descendants(rating), d => d is TextBlock { Text: "¡Genial!" }); // la leyenda sigue a la vista previa

        var panel = (Panel)VisualTreeHelper.GetParent(stars[0]);
        panel.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        Assert.Equal(new[] { true, true, false, false, false }, Lit(stars));
        Assert.Contains(Descendants(rating), d => d is TextBlock { Text: "Mal" });
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(Key.Right, 0, 1)]
    [InlineData(Key.Right, 3, 4)]
    [InlineData(Key.Right, 5, 5)]
    [InlineData(Key.Left, 3, 2)]
    [InlineData(Key.Left, 1, 1)]
    [InlineData(Key.Left, 0, 1)]
    [InlineData(Key.Home, 4, 1)]
    [InlineData(Key.End, 1, 5)]
    [InlineData(Key.D1, 5, 1)]
    [InlineData(Key.D5, 0, 5)]
    [InlineData(Key.NumPad3, 0, 3)]
    public void Keyboard_MovesTheRating(Key key, int current, int expected)
        => Assert.Equal(expected, StarRating.KeyTarget(key, current));

    [Theory]
    [InlineData(Key.D6)]
    [InlineData(Key.D0)]
    [InlineData(Key.Up)]
    [InlineData(Key.Enter)]
    [InlineData(Key.A)]
    public void Keyboard_IgnoresOtherKeys(Key key) => Assert.Null(StarRating.KeyTarget(key, 3));

    [Fact]
    public Task Keyboard_ByDefault_ChoosesAtOnce() => WpfHost.RunAsync(() =>
    {
        // La encuesta: las flechas cambian la valoración (y avisan al comando).
        var picked = new List<object?>();
        var rating = new StarRating { RatedCommand = new RelayCommand(p => picked.Add(p)) };
        var stars = Stars(rating);
        Assert.True(rating.CommitOnNavigation);

        Assert.True(rating.HandleKey(Key.Right));
        Assert.Equal(1, rating.Value);
        Assert.True(rating.HandleKey(Key.End));
        Assert.Equal(5, rating.Value);
        Assert.Equal(new object?[] { 1, 5 }, picked);
        Assert.False(rating.HandleKey(Key.Enter)); // Enter/Espacio los atiende la estrella como un clic
        Assert.Equal(new[] { true, true, true, true, true }, Lit(stars));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Keyboard_WithoutCommit_OnlyMovesThePreview_UntilEnterSpaceOrClick() => WpfHost.RunAsync(() =>
    {
        // La invitación tras la partida: elegir abre la encuesta, así que las flechas no deben elegir.
        var picked = new List<object?>();
        var rating = new StarRating { CommitOnNavigation = false, RatedCommand = new RelayCommand(p => picked.Add(p)) };
        var stars = Stars(rating);

        Assert.True(rating.HandleKey(Key.Right));
        Assert.Equal(0, rating.Value);
        Assert.Equal(new[] { true, false, false, false, false }, Lit(stars)); // vista previa
        Assert.True(rating.HandleKey(Key.Right));
        Assert.True(rating.HandleKey(Key.Right));
        Assert.Equal(new[] { true, true, true, false, false }, Lit(stars));
        Assert.True(rating.HandleKey(Key.Left));
        Assert.Equal(new[] { true, true, false, false, false }, Lit(stars));
        Assert.True(rating.HandleKey(Key.End));
        Assert.True(rating.HandleKey(Key.D4));
        Assert.True(rating.HandleKey(Key.Home));
        Assert.Equal(new[] { true, false, false, false, false }, Lit(stars));
        Assert.Equal(0, rating.Value);
        Assert.Empty(picked);
        Assert.False(rating.HandleKey(Key.Enter));
        Assert.False(rating.HandleKey(Key.Space));

        // Enter/Espacio sobre una estrella llegan como su clic: ahí sí se elige.
        Click(stars[3]);
        Assert.Equal(4, rating.Value);
        Assert.Equal(new object?[] { 4 }, picked);
        return Task.CompletedTask;
    });

    [Fact]
    public Task FeedbackPrompt_StarsDoNotChooseWithTheArrowKeys() => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        var window = new MainWindow(vm);
        try
        {
            await WpfHost.FlushAsync(); // enlaces diferidos
            var stars = (StarRating)window.FindName("FeedbackPromptStars");
            Assert.False(stars.CommitOnNavigation);
            Assert.Same(vm.OpenFeedbackFromPromptCommand, stars.RatedCommand);

            vm.ShowFeedbackPrompt(30, 0);
            Assert.True(stars.HandleKey(Key.Right));
            Assert.True(vm.IsFeedbackPromptVisible); // la encuesta no se abrió
            Assert.Equal(0, vm.FeedbackPromptRating);
            await WpfHost.FlushAsync();
            Assert.Null(vm.Dialogs.CurrentDialog);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task OnlyOneStarIsATabStop() => WpfHost.RunAsync(() =>
    {
        var rating = new StarRating();
        var stars = Stars(rating);
        Assert.Equal(new[] { true, false, false, false, false }, stars.Select(KeyboardNavigation.GetIsTabStop));
        rating.Value = 4;
        Assert.Equal(new[] { false, false, false, true, false }, stars.Select(KeyboardNavigation.GetIsTabStop));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Value_IsClamped_AndStarsHaveAccessibleNames() => WpfHost.RunAsync(() =>
    {
        var rating = new StarRating { Value = 9 };
        Assert.Equal(5, rating.Value);
        rating.Value = -1;
        Assert.Equal(0, rating.Value);

        var stars = Stars(rating);
        Assert.Equal("1 estrella", AutomationProperties.GetName(stars[0]));
        Assert.Equal("5 estrellas", AutomationProperties.GetName(stars[4]));
        Assert.Equal("Bien", AutomationProperties.GetHelpText(stars[3]));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TwoWayBinding_WithTheRatingQuestion() => WpfHost.RunAsync(() =>
    {
        var question = new RatingQuestionViewModel(new SurveyQuestion { Id = "overall", Type = SurveyQuestionType.Rating, Text = "¿Qué tal?" }, 1);
        var rating = new StarRating { DataContext = question };
        rating.SetBinding(StarRating.ValueProperty, new Binding(nameof(RatingQuestionViewModel.Value)));
        var stars = Stars(rating);

        Click(stars[3]);
        Assert.Equal(4, question.Value); // del control a la pregunta

        question.Value = 2;
        Assert.Equal(2, rating.Value);   // y de la pregunta al control
        Assert.Equal(new[] { true, true, false, false, false }, Lit(stars));

        // Con un binding OneWay, elegir no lo rompe (SetCurrentValue).
        var oneWay = new StarRating { DataContext = question };
        oneWay.SetBinding(StarRating.ValueProperty, new Binding(nameof(RatingQuestionViewModel.Value)) { Mode = BindingMode.OneWay });
        Click(Stars(oneWay)[4]);
        Assert.Equal(5, oneWay.Value);
        Assert.NotNull(BindingOperations.GetBindingExpression(oneWay, StarRating.ValueProperty));
        return Task.CompletedTask;
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }
}
