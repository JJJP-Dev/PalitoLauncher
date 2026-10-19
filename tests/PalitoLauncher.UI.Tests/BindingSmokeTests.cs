using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;
using PalitoLauncher.Views;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Construye MainWindow y todas las vistas fuera de pantalla (sin Show: Measure/Arrange a mano) con la App y el
/// Theme.xaml reales, recorre las páginas y los diálogos y falla con cualquier error de binding (System.Windows.Data).
/// </summary>
public sealed class BindingSmokeTests
{
    private const double Width = 1120;
    private const double Height = 680;

    [Fact]
    public Task Listener_DetectsABrokenBinding() => WpfHost.RunAsync(async () =>
    {
        // Comprueba que la captura funciona: si no, el resto de tests pasaría sin mirar nada.
        using var errors = BindingErrors.Capture();
        var text = new TextBlock { DataContext = new object() };
        text.SetBinding(TextBlock.TextProperty, new Binding("PropiedadQueNoExiste"));
        Layout(text);
        await WpfHost.FlushAsync();

        Assert.Contains(errors.Messages, m => m.Contains("PropiedadQueNoExiste", StringComparison.Ordinal));
    });

    [Fact]
    public Task MainWindow_PagesMenuAndDialogs_HaveNoBindingErrors() => WpfHost.RunAsync(async () =>
    {
        using var temp = new TempDir();
        using var errors = BindingErrors.Capture();
        using var vm = new MainViewModel();
        var releases = SampleReleases();
        await MainViewModelAccess.ApplyReleasesAsync(vm, releases);
        vm.Releases[0].IsExpanded = true; // notas de la versión dentro de la plantilla de la lista
        vm.ShowToast("Mensaje de prueba");

        // 1.0.3: roadmap con datos (y un hito expandido), invitación a opinar, aviso de cierre con «Contarnos qué pasó»
        // y opiniones pendientes en Ajustes.
        vm.ApplyRoadmapResult(TestFeedback.Result());
        vm.RoadmapItems[1].IsExpanded = true;
        vm.ShowFeedbackPrompt(34, 0);
        MainViewModelAccess.CrashNotice(vm) = "Coop2D se cerró de forma inesperada (código 3).";
        MainViewModelAccess.UpdateState(vm);
        vm.Settings.SetPendingFeedback(2);

        var window = new MainWindow(vm);
        var root = (FrameworkElement)window.Content;
        try
        {
            await LayoutAsync(window);
            Assert.Contains(Descendants(root), d => d is HomeView);
            AssertShowsText(root, vm.StatusText);
            AssertShowsText(root, "En desarrollo");                 // tarjeta del roadmap en Inicio
            AssertShowsText(root, vm.RoadmapCurrent[0].Title);
            AssertShowsText(root, vm.FeedbackPromptTitle);          // invitación tras la partida
            Assert.Contains(Descendants(root), d => d is StarRating);
            AssertShowsText(root, "Contarnos qué pasó");            // segunda acción del aviso

            vm.NavigateCommand.Execute("Versions");
            await LayoutAsync(window);
            Assert.True(vm.IsVersionsPage);
            AssertShowsText(root, vm.Releases[0].VersionText); // la plantilla de la lista se generó de verdad
            Assert.Contains(Descendants(root), d => d is FlowDocumentScrollViewer { Document: not null });

            vm.NavigateCommand.Execute("Roadmap");
            await LayoutAsync(window);
            Assert.True(vm.IsRoadmapPage);
            Assert.Contains(Descendants(root), d => d is RoadmapView);
            AssertShowsText(root, vm.RoadmapSummaryText);
            AssertShowsText(root, vm.RoadmapPhases[0].Title);
            AssertShowsText(root, vm.RoadmapItems[0].ProgressText);
            Assert.Contains(Descendants(root), d => d is FlowDocumentScrollViewer { Document: { } doc } && ReferenceEquals(doc, vm.RoadmapItems[1].DescriptionDocument));

            vm.NavigateCommand.Execute("Settings");
            await LayoutAsync(window);
            Assert.True(vm.IsSettingsPage);
            AssertShowsText(root, vm.Settings.LauncherVersionText);
            AssertShowsText(root, vm.Settings.PendingFeedbackText);

            vm.NavigateCommand.Execute("Home");
            await LayoutAsync(window);

            // Menú ⋯ (la ventana le pasa el DataContext al abrirlo).
            var more = (Button)window.FindName("MoreButton");
            var menu = more.ContextMenu!;
            menu.DataContext = vm;
            await LayoutAsync(menu, 320, 400);
            Assert.NotEmpty(Descendants(menu).OfType<MenuItem>());

            // Diálogo de mensaje con los tres botones.
            var message = vm.Dialogs.ShowAsync("Título", "Mensaje largo de prueba", "Aceptar", "Cancelar", isDanger: true, tertiaryText: "Otra");
            await LayoutAsync(window);
            Assert.Contains(Descendants(root), d => d is MessageDialogView);
            ((MessageDialogViewModel)vm.Dialogs.CurrentDialog!).Cancel();
            Assert.Equal(DialogResult.Secondary, await message);

            // Diálogo de instalación (validado contra una carpeta temporal).
            var dialog = new InstallDialogViewModel(releases[0], temp.Path, _ => new InstallStatus { State = InstallState.NotInstalled },
                showShortcutOption: true);
            var shown = vm.Dialogs.ShowDialogAsync(dialog, dialog.Completion);
            dialog.StartValidation();
            await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "validación del diálogo de instalación");
            await LayoutAsync(window);
            Assert.Contains(Descendants(root), d => d is InstallDialogView);
            AssertShowsText(root, dialog.ResolvedPathText);
            dialog.Cancel();
            Assert.Null(await shown);
            await LayoutAsync(window);

            // Encuesta: todas las plantillas, error de envío y confirmación de descartar.
            var survey = new SurveyDialogViewModel(TestFeedback.Survey(),
                new SurveyContext { Trigger = FeedbackTrigger.AfterSession, InitialRating = 4, LauncherVersion = "1.0.3", SessionMinutes = 34 },
                _ => throw new LauncherException("Error de envío de prueba."));
            var surveyShown = vm.Dialogs.ShowDialogAsync(survey, survey.Completion);
            await LayoutAsync(window);
            Assert.Contains(Descendants(root), d => d is SurveyDialogView);
            AssertShowsText(root, survey.Title);
            AssertShowsText(root, survey.MetaText);
            Assert.Contains(Descendants(root), d => d is RadioButton);
            Assert.Contains(Descendants(root), d => d is CheckBox);
            Assert.Contains(Descendants(root), d => d is TextBox { AcceptsReturn: true });

            survey.Questions.OfType<ChoiceQuestionViewModel>().First().Options[0].IsSelected = true;
            await ((AsyncRelayCommand)survey.SendCommand).ExecuteAsync(null);
            await LayoutAsync(window);
            AssertShowsText(root, "Error de envío de prueba.");

            survey.Cancel();
            await LayoutAsync(window);
            AssertShowsText(root, "¿Descartar tus respuestas?");
            var exit = survey.ConfirmExitAsync(); // cerrar el launcher con la opinión a medias
            await LayoutAsync(window);
            AssertShowsText(root, "Tienes una opinión sin enviar. ¿Salir sin enviarla?");
            survey.DiscardCommand.Execute(null);
            Assert.True(await exit);
            Assert.Null(await surveyShown);
            await LayoutAsync(window);

            Assert.Equal(IntPtr.Zero, new WindowInteropHelper(window).Handle); // nunca se creó la ventana de Windows
        }
        finally
        {
            window.Close();
        }

        await WpfHost.FlushAsync();
        Assert.True(errors.Messages.Count == 0, string.Join(Environment.NewLine, errors.Messages)); // el mensaje completo, no truncado
        Assert.Empty(WpfHost.UnhandledExceptions);
    });

    public static TheoryData<string> Views => new()
    {
        "Home", "Versions", "Settings", "InstallDialog", "InstallDialogBlocked", "MessageDialog",
        "Roadmap", "RoadmapLoading", "SurveyDialog",
    };

    [Theory]
    [MemberData(nameof(Views))]
    public Task EachView_Standalone_HasNoBindingErrors(string view) => WpfHost.RunAsync(async () =>
    {
        using var temp = new TempDir();
        using var errors = BindingErrors.Capture();
        using var vm = new MainViewModel();
        var releases = SampleReleases();
        await MainViewModelAccess.ApplyReleasesAsync(vm, releases);
        foreach (var item in vm.Releases) item.IsExpanded = true;
        if (view != "RoadmapLoading")
        {
            vm.ApplyRoadmapResult(TestFeedback.Result());
            foreach (var item in vm.RoadmapItems) item.IsExpanded = true;
        }
        vm.Settings.SetPendingFeedback(1, waitsForTeam: true); // la variante neutra (sin webhook); la de aviso va en el otro test

        FrameworkElement element;
        switch (view)
        {
            case "Home":
                element = new HomeView { DataContext = vm };
                break;
            case "Versions":
                element = new VersionsView { DataContext = vm };
                break;
            case "Settings":
                element = new SettingsView { DataContext = vm.Settings };
                break;
            case "Roadmap":
            case "RoadmapLoading":
                element = new RoadmapView { DataContext = vm };
                break;
            case "SurveyDialog":
                element = new SurveyDialogView
                {
                    DataContext = new SurveyDialogViewModel(TestFeedback.Survey(),
                        new SurveyContext { InitialRating = 3, Nickname = "Palito", LauncherVersion = "1.0.3", GameVersionDisplay = "0.0.3 · Prototipo 2" },
                        _ => Task.FromResult(FeedbackSendOutcome.Sent)),
                };
                break;
            case "InstallDialog":
            case "InstallDialogBlocked":
            {
                // Bloqueada: ruta relativa → mensaje de error visible (otros triggers de estilo).
                var folder = view == "InstallDialog" ? temp.Path : "carpeta-relativa";
                var dialog = new InstallDialogViewModel(releases[0], folder, _ => new InstallStatus { State = InstallState.NotInstalled },
                    showShortcutOption: true);
                dialog.StartValidation();
                await WpfHost.WaitUntilAsync(() => !dialog.IsValidating, "validación del diálogo de instalación");
                Assert.Equal(view == "InstallDialogBlocked", dialog.IsLocationBlocked);
                element = new InstallDialogView { DataContext = dialog };
                break;
            }
            default:
                element = new MessageDialogView
                {
                    DataContext = new MessageDialogViewModel("Título", "Mensaje", "Aceptar", "Cancelar", isDanger: false, tertiaryText: "Otra"),
                };
                break;
        }

        await LayoutAsync(element);
        Assert.True(Descendants(element).Count() > 10, "La vista no generó su árbol visual: la prueba no estaría mirando nada.");
        await WpfHost.FlushAsync();
        Assert.True(errors.Messages.Count == 0, string.Join(Environment.NewLine, errors.Messages)); // el mensaje completo, no truncado
    });

    private static IReadOnlyList<GameRelease> SampleReleases() => new[]
    {
        TestReleases.Make("v0.0.3", releaseId: 3, assetId: 30, sha256: "c3", published: TestReleases.BaseDate.AddDays(2),
            body: "## Novedades\n- **Nuevo** nivel\n- Ver [notas](https://github.com/JJJP-Dev/Coop2D)"),
        TestReleases.Make("v0.0.2", releaseId: 2, assetId: 20, sha256: "c2", published: TestReleases.BaseDate.AddDays(1), prerelease: true,
            body: "Arreglos varios."),
        TestReleases.Make("v0.0.1", releaseId: 1, assetId: 10, sha256: "c1", published: TestReleases.BaseDate, body: null),
    };

    /// <summary>
    /// Measure/Arrange a mano. Una Window sin mostrar no hace layout (lo dirige su HWND): se maqueta su contenido, que
    /// sigue siendo hijo lógico de la ventana (hereda el DataContext y encuentra los DataTemplate de Window.Resources).
    /// </summary>
    private static void Layout(UIElement element, double width = Width, double height = Height)
    {
        if (element is Window window) element = (UIElement)window.Content;
        else if (element is Control control) control.ApplyTemplate();
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static async Task LayoutAsync(UIElement element, double width = Width, double height = Height)
    {
        Layout(element, width, height);
        await WpfHost.FlushAsync();
        Layout(element, width, height); // lo que se generó al vaciar el Dispatcher (plantillas diferidas)
    }

    /// <summary>
    /// Hay un TextBlock con ese texto en el árbol visual (IsVisible no sirve: sin ventana mostrada siempre es false). Las
    /// plantillas de lo que está Collapsed no se generan, así que esto prueba que esa parte se construyó y se enlazó.
    /// </summary>
    private static void AssertShowsText(DependencyObject root, string text)
    {
        Assert.False(string.IsNullOrEmpty(text));
        Assert.Contains(Descendants(root), d => d is TextBlock tb && tb.Text == text);
    }

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
