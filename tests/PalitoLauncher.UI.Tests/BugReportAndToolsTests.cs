using System.IO;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Services;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// 1.0.5 (UI-2): «Reportar un problema» (validación del diálogo, envío real a la bandeja de salida sin webhook y sin red,
/// entradas desde el aviso de cierre y el diálogo de error, cierre del launcher con un reporte a medias), perfiles de
/// lanzamiento combinados con los argumentos, «Hostear partida» (con IP y portapapeles falsos) y modo desarrollador
/// (con un lanzador falso: nunca abre procesos).
/// </summary>
public sealed class BugReportAndToolsTests
{
    private static Task WithViewModel(Func<MainViewModel, Task> test) => WpfHost.RunAsync(async () =>
    {
        try
        {
            using var vm = new MainViewModel();
            MainViewModelAccess.Survey(vm) = TestFeedback.Survey(); // encuesta ya "cargada" y sin webhook: nunca toca la red
            await test(vm);
        }
        finally
        {
            DeleteOutbox();
        }
    });

    private static void DeleteOutbox()
    {
        try
        {
            var reports = Path.GetDirectoryName(AppPaths.BugReportOutboxDir)!;
            if (Directory.Exists(reports)) Directory.Delete(reports, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static BugReportDialogViewModel NewDialog(Func<BugReport, bool, Task<FeedbackSendOutcome>>? submit = null,
        BugCategory category = BugCategory.Bug) => new(
        new BugReportContext
        {
            Category = category,
            LauncherVersion = "1.0.5",
            GameVersion = "0.0.3-prototype.2",
            GameVersionDisplay = "0.0.3 · Prototipo 2",
            Nickname = " Palito ",
            OsDescription = "Microsoft Windows 10.0.26200",
            Attachments = MainViewModel.BuildAttachmentPreviews(new MainViewModel.BugLogPaths(null, null, null)),
        },
        submit ?? ((_, _) => Task.FromResult(FeedbackSendOutcome.Sent)));

    private static async Task<T> WaitForDialogAsync<T>(MainViewModel vm) where T : class
    {
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog is T, typeof(T).Name);
        return (T)vm.Dialogs.CurrentDialog!;
    }

    private static async Task CloseReportAsync(MainViewModel vm, BugReportDialogViewModel dialog)
    {
        dialog.Cancel();
        if (dialog.IsConfirmingDiscard) dialog.DiscardCommand.Execute(null);
        Assert.Null(await dialog.Completion);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");
    }

    // ───────────── Diálogo «Reportar un problema» ─────────────

    [Fact]
    public Task Dialog_NeedsTenCharacters_AndBuildsTheReport() => WpfHost.RunAsync(async () =>
    {
        BugReport? sent = null;
        bool? attached = null;
        var dialog = NewDialog((report, attach) =>
        {
            sent = report;
            attached = attach;
            return Task.FromResult(FeedbackSendOutcome.Queued);
        });

        Assert.Equal("Reportar un problema", dialog.Title);
        Assert.Equal(BugReportDialogViewModel.DefaultIntro, dialog.Intro);
        Assert.Equal("Coop2D 0.0.3 · Prototipo 2 · Launcher 1.0.5", dialog.MetaText);
        Assert.Equal(BugCategory.Bug, dialog.SelectedCategory);
        Assert.Equal(Enum.GetValues<BugCategory>().Length, dialog.Categories.Count);
        Assert.Equal(dialog.Categories.Select(c => BugReportService.CategoryDisplayName(c.Category)), dialog.Categories.Select(c => c.Text));
        Assert.Single(dialog.Categories, c => c.IsSelected);
        Assert.True(dialog.AttachLogs); // marcada por defecto
        Assert.Equal("Palito", dialog.Nickname);
        Assert.Equal(new[] { "Player.log", "Player-prev.log", "launcher.log", "summary.txt" }, dialog.Attachments.Select(a => a.Name));

        // Vacío o corto: no se puede enviar y se dice cuánto falta.
        Assert.False(dialog.CanSend);
        Assert.False(dialog.SendCommand.CanExecute(null));
        Assert.Equal("Escribe al menos 10 caracteres", dialog.DescriptionHint);
        dialog.Description = "   corto   ";
        Assert.Equal("Faltan 5 caracteres", dialog.DescriptionHint);
        Assert.Equal("11 / 2000", dialog.DescriptionCounterText);
        Assert.False(dialog.CanSend);
        dialog.Description = "123456789";
        Assert.Equal("Falta 1 carácter", dialog.DescriptionHint);

        dialog.Description = "  Se congela al abrir la puerta del jefe  ";
        Assert.True(dialog.IsDescriptionValid);
        Assert.True(dialog.CanSend);
        Assert.Equal("", dialog.DescriptionHint);
        Assert.False(dialog.ShowDescriptionHint);

        // Una sola categoría a la vez.
        dialog.Categories.Single(c => c.Category == BugCategory.Network).IsSelected = true;
        Assert.Equal(BugCategory.Network, dialog.SelectedCategory);
        Assert.Single(dialog.Categories, c => c.IsSelected);

        // Límites: la descripción y los pasos se recortan a su máximo.
        dialog.Steps = new string('x', 1500);
        Assert.Equal(BugReportDialogViewModel.MaxStepsLength, dialog.Steps.Length);
        Assert.True(dialog.IsStepsNearLimit);
        dialog.Steps = "  1. Crear partida\n2. Abrir la puerta  ";
        dialog.AttachLogs = false;
        Assert.True(dialog.AttachmentsOpacity < 1);

        await ((AsyncRelayCommand)dialog.SendCommand).ExecuteAsync(null);
        Assert.Equal(FeedbackSendOutcome.Queued, (await dialog.Completion)!.Outcome);
        Assert.NotNull(sent);
        Assert.Equal(BugCategory.Network, sent!.Category);
        Assert.Equal("Se congela al abrir la puerta del jefe", sent.Description);
        Assert.Equal("1. Crear partida\n2. Abrir la puerta", sent.Steps);
        Assert.Equal("Palito", sent.Nickname);
        Assert.Equal("0.0.3-prototype.2", sent.GameVersion);
        Assert.Equal("0.0.3 · Prototipo 2", sent.GameVersionDisplay);
        Assert.Equal("1.0.5", sent.LauncherVersion);
        Assert.Null(sent.ExitCode);
        Assert.False(attached);
        Assert.False(dialog.HasUnsentText); // ya enviado: cerrar el launcher no pregunta
    });

    [Fact]
    public Task Dialog_Escape_ClosesWhenEmpty_AsksWhenThereIsText() => WpfHost.RunAsync(async () =>
    {
        var empty = NewDialog();
        empty.Cancel();
        Assert.Null(await empty.Completion);

        var dialog = NewDialog();
        dialog.Steps = "Solo los pasos";
        Assert.True(dialog.HasText);
        dialog.Cancel();
        Assert.True(dialog.IsConfirmingDiscard);
        Assert.Equal("¿Descartar el reporte?", dialog.ConfirmText);
        Assert.Equal("Seguir escribiendo", dialog.KeepEditingText);
        Assert.False(dialog.CanSend);

        dialog.Cancel(); // Esc otra vez = seguir escribiendo
        Assert.False(dialog.IsConfirming);
        Assert.False(dialog.Completion.IsCompleted);

        dialog.Cancel();
        dialog.DiscardCommand.Execute(null);
        Assert.Null(await dialog.Completion);
    });

    [Fact]
    public Task Dialog_SendError_KeepsEverything_AndRetriesWithTheSameId() => WpfHost.RunAsync(async () =>
    {
        var ids = new List<string>();
        var dialog = NewDialog((report, _) =>
        {
            ids.Add(report.Id);
            if (ids.Count == 1) throw new LauncherException("Espera un momento antes de enviar otro reporte.");
            return Task.FromResult(FeedbackSendOutcome.Sent);
        });
        dialog.Description = "El jefe no aparece nunca";

        await ((AsyncRelayCommand)dialog.SendCommand).ExecuteAsync(null);
        Assert.True(dialog.HasError);
        Assert.Equal("Espera un momento antes de enviar otro reporte.", dialog.ErrorMessage);
        Assert.False(dialog.Completion.IsCompleted);
        Assert.Equal("El jefe no aparece nunca", dialog.Description);
        Assert.False(dialog.IsSending);

        await ((AsyncRelayCommand)dialog.SendCommand).ExecuteAsync(null);
        Assert.Equal(FeedbackSendOutcome.Sent, (await dialog.Completion)!.Outcome);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]); // el reintento sustituye al mismo reporte en la bandeja (no cuenta como otro)
    });

    [Fact]
    public Task Dialog_ConfirmExit_FromClosingTheLauncher() => WpfHost.RunAsync(async () =>
    {
        var dialog = NewDialog();
        Assert.True(await dialog.ConfirmExitAsync()); // sin texto: nada que perder

        dialog.Description = "Algo raro en el menú";
        var leave = dialog.ConfirmExitAsync();
        Assert.True(dialog.IsConfirmingExit);
        Assert.Equal("Tienes un reporte sin enviar. ¿Salir sin enviarlo?", dialog.ConfirmText);
        Assert.Equal("Salir", dialog.ConfirmActionText);
        dialog.KeepEditingCommand.Execute(null);
        Assert.False(await leave);

        leave = dialog.ConfirmExitAsync();
        dialog.DiscardCommand.Execute(null);
        Assert.True(await leave);
        Assert.Null(await dialog.Completion);
    });

    [Fact]
    public void AttachmentPreviews_ShowRealSizes_AndWhatIsMissing()
    {
        using var temp = new TempDir();
        var small = Path.Combine(temp.Path, "Player.log");
        File.WriteAllText(small, new string('a', 1500));
        var big = Path.Combine(temp.Path, "launcher.log");
        using (var stream = File.Create(big)) stream.SetLength(3 * 1024 * 1024);

        var previews = MainViewModel.BuildAttachmentPreviews(new MainViewModel.BugLogPaths(small, Path.Combine(temp.Path, "no-existe.log"), big));

        Assert.True(previews[0].Exists);
        Assert.Equal(FileSystemUtil.FormatBytes(1500), previews[0].SizeText);
        Assert.False(previews[1].Exists);
        Assert.Equal("no encontrado", previews[1].SizeText);
        Assert.StartsWith("últimos ", previews[2].SizeText); // solo se envía el final de un log grande
        Assert.EndsWith(FileSystemUtil.FormatBytes(3 * 1024 * 1024), previews[2].SizeText);
        Assert.Equal("summary.txt", previews[3].Name);
    }

    // ───────────── Envío desde el MainViewModel (bandeja de salida, sin red) ─────────────

    [Fact]
    public Task ReportProblem_WithoutAWebhook_IsSavedInTheOutbox_AndSettingsSaySo() => WithViewModel(async vm =>
    {
        Assert.True(vm.ReportProblemCommand.CanExecute(null)); // aunque el juego no esté instalado
        vm.ReportProblemCommand.Execute(null);
        var dialog = await WaitForDialogAsync<BugReportDialogViewModel>(vm);
        Assert.Equal(BugCategory.Bug, dialog.SelectedCategory);
        Assert.False(vm.ReportProblemCommand.CanExecute(null)); // uno a la vez
        Assert.Equal("Coop2D no instalado · Launcher " + Helpers.AppInfo.VersionText, dialog.MetaText);

        dialog.Description = "El personaje atraviesa las paredes del nivel 2";
        dialog.Nickname = " Tester ";
        await ((AsyncRelayCommand)dialog.SendCommand).ExecuteAsync(null);
        Assert.False(dialog.HasError, dialog.ErrorMessage);
        Assert.Equal(FeedbackSendOutcome.Queued, (await dialog.Completion)!.Outcome);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");

        // Sin webhook (ni en launcher.json ni en la encuesta) no es cosa de la conexión: el toast y Ajustes lo dicen así.
        Assert.Equal(MainViewModel.BugReportQueuedNoWebhookToast, vm.ToastText);
        Assert.Equal("Tester", vm.CurrentSettings.FeedbackNickname); // el nombre es el mismo que el de las opiniones
        await WpfHost.WaitUntilAsync(() => vm.Settings.HasPendingBugReports, "reportes pendientes en Ajustes");
        Assert.True(vm.Settings.PendingBugReportsWaitForTeam);
        Assert.Equal("1 reporte de problema pendiente: se enviará cuando el equipo active los envíos", vm.Settings.PendingBugReportsText);

        // En la bandeja: report.json y los adjuntos anonimizados (aquí solo el resumen: no hay juego ni log del launcher).
        var folder = Assert.Single(Directory.GetDirectories(AppPaths.BugReportOutboxDir), d => !Path.GetFileName(d).StartsWith('.'));
        Assert.True(File.Exists(Path.Combine(folder, "report.json")));
        var summary = File.ReadAllText(Path.Combine(folder, "summary.txt"));
        Assert.Contains("El personaje atraviesa las paredes", summary);
        Assert.Contains("Origen del reporte: manual", summary);
        Assert.True(vm.ReportProblemCommand.CanExecute(null));

        // «Reintentar envío» sin webhook: dice la verdad y no pierde nada.
        await ((AsyncRelayCommand)vm.RetryBugReportSendCommand).ExecuteAsync(null);
        Assert.Equal("Todavía no se pueden enviar. Se enviarán solos en cuanto el equipo active los envíos.", vm.ToastText);
        Assert.True(vm.Settings.HasPendingBugReports);
    });

    [Fact]
    public Task CrashNotice_ReportTheCrash_SendsACrashReport_WithThePlayedVersion() => WithViewModel(async vm =>
    {
        MainViewModelAccess.StartSession(vm, "0.0.3-prototype.2", "0.0.3 · Prototipo 2", minutes: 12);
        await MainViewModelAccess.HandleGameExitedAsync(vm, 3);
        Assert.Equal("Reportar el cierre", vm.SecondaryNoticeActionText);
        Assert.Equal("Contarnos qué pasó", vm.SecondaryNoticeAction2Text);

        MainViewModelAccess.InstallStatus(vm) = MainViewModelAccess.Installed("0.0.4", "0.0.4"); // actualizó antes de contarlo
        vm.SecondaryNoticeActionCommand.Execute(null);
        var dialog = await WaitForDialogAsync<BugReportDialogViewModel>(vm);
        Assert.Equal(BugCategory.Crash, dialog.SelectedCategory);
        Assert.Contains("(código 3)", dialog.Intro);
        var report = dialog.BuildReport();
        Assert.Equal(3, report.ExitCode);
        Assert.Equal("0.0.3-prototype.2", report.GameVersion);

        dialog.Description = "Se cerró al lanzar la granada";
        await ((AsyncRelayCommand)dialog.SendCommand).ExecuteAsync(null);
        Assert.Equal(FeedbackSendOutcome.Queued, (await dialog.Completion)!.Outcome);

        // Ya está contado: el aviso de cierre deja de verse.
        await WpfHost.WaitUntilAsync(() => MainViewModelAccess.CrashNotice(vm) == null, "aviso de cierre descartado");
        Assert.NotEqual("Reportar el cierre", vm.SecondaryNoticeActionText);
    });

    [Fact]
    public Task ErrorDialog_OffersToReport_ButEscapeNeverReports() => WithViewModel(async vm =>
    {
        var shown = vm.ShowUnexpectedErrorAsync();
        var message = await WaitForDialogAsync<MessageDialogViewModel>(vm);
        Assert.Equal("Reportar el problema", message.SecondaryText);
        Assert.Equal("Crear informe", message.TertiaryText);
        Assert.Equal("Aceptar", message.PrimaryText);

        message.Cancel(); // Esc = cerrar, no reportar
        Assert.Equal(DialogResult.None, await message.Completion);
        await shown;
        await WpfHost.FlushAsync();
        Assert.Null(vm.Dialogs.CurrentDialog);

        shown = vm.ShowUnexpectedErrorAsync();
        message = await WaitForDialogAsync<MessageDialogViewModel>(vm);
        message.SecondaryCommand.Execute(null);
        await shown;
        var dialog = await WaitForDialogAsync<BugReportDialogViewModel>(vm);
        Assert.Equal(BugCategory.Launcher, dialog.SelectedCategory);
        Assert.Contains("error", dialog.Intro);
        await CloseReportAsync(vm, dialog);
    });

    [Fact]
    public async Task MessageDialog_EscapeChoosesSecondary_ByDefault()
    {
        var normal = new MessageDialogViewModel("T", "M", "Aceptar", "Cancelar", isDanger: false, tertiaryText: null);
        normal.Cancel();
        Assert.Equal(DialogResult.Secondary, await normal.Completion);

        var action = new MessageDialogViewModel("T", "M", "Aceptar", "Reportar", isDanger: false, tertiaryText: null, escapeChoosesSecondary: false);
        action.Cancel();
        Assert.Equal(DialogResult.None, await action.Completion);
    }

    [Fact]
    public Task Closing_WithAHalfWrittenReport_AsksFirst() => WithViewModel(async vm =>
    {
        vm.ReportProblemCommand.Execute(null);
        var dialog = await WaitForDialogAsync<BugReportDialogViewModel>(vm);

        // Abierto pero vacío: se puede cerrar sin preguntar.
        Assert.False(vm.HasUnsentFeedback);
        Assert.Equal(CloseDecision.Allow, vm.GetCloseDecision());

        dialog.Description = "Me echa de la partida a los 5 minutos";
        Assert.True(vm.HasUnsentFeedback);
        Assert.Equal(CloseDecision.ConfirmUnsentFeedback, vm.GetCloseDecision());

        // «Volver»: no se cierra y lo escrito sigue ahí.
        var leave = vm.ConfirmCloseWithUnsentFeedbackAsync();
        Assert.True(dialog.IsConfirmingExit);
        dialog.KeepEditingCommand.Execute(null);
        Assert.False(await leave);
        Assert.Same(dialog, vm.Dialogs.CurrentDialog);

        // «Salir»: se descarta y ya se puede cerrar.
        leave = vm.ConfirmCloseWithUnsentFeedbackAsync();
        dialog.DiscardCommand.Execute(null);
        Assert.True(await leave);
        Assert.Equal(CloseDecision.Allow, vm.GetCloseDecision());
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");
    });

    [Fact]
    public Task ReportProblem_CannotBeOpenedWhileTheLauncherUpdates() => WithViewModel(async vm =>
    {
        MainViewModelAccess.OperationKind(vm) = OperationKind.LauncherUpdate;
        try
        {
            Assert.False(vm.ReportProblemCommand.CanExecute(null));
            var shown = vm.ShowUnexpectedErrorAsync();
            var message = await WaitForDialogAsync<MessageDialogViewModel>(vm);
            message.SecondaryCommand.Execute(null);
            await shown;
            await WpfHost.FlushAsync();
            Assert.Null(vm.Dialogs.CurrentDialog);
            Assert.Contains("actualización del launcher", vm.ToastText);
        }
        finally
        {
            MainViewModelAccess.OperationKind(vm) = OperationKind.None;
        }
    });

    [Fact]
    public void BugReportToast_TellsTheRealReason()
    {
        Assert.Equal(MainViewModel.BugReportSentToast, MainViewModel.BugReportResultToast(FeedbackSendOutcome.Sent, hasWebhook: false));
        Assert.Equal(MainViewModel.BugReportQueuedToast, MainViewModel.BugReportResultToast(FeedbackSendOutcome.Queued, hasWebhook: true));
        Assert.Equal(MainViewModel.BugReportQueuedNoWebhookToast, MainViewModel.BugReportResultToast(FeedbackSendOutcome.Queued, hasWebhook: false));
    }

    [Theory]
    [InlineData(1, 1, 0, true, "Listo: se envió el reporte pendiente.")]
    [InlineData(3, 3, 0, true, "Listo: se enviaron todos los reportes pendientes.")]
    [InlineData(2, 0, 2, true, "No se pudieron enviar ahora. Se reintentará automáticamente más tarde.")]
    [InlineData(2, 0, 2, false, "Todavía no se pueden enviar. Se enviarán solos en cuanto el equipo active los envíos.")]
    [InlineData(3, 1, 2, true, "Se envió 1 reporte. 2 siguen pendientes y se reintentarán más tarde.")]
    [InlineData(2, 1, 0, true, "Se envió 1 reporte. 1 no se pudo enviar y se ha apartado (no se volverá a intentar).")]
    public void RetryBugReportToast_SaysWhatHappened(int before, int sent, int after, bool hasWebhook, string expected)
        => Assert.Equal(expected, MainViewModel.BuildRetryBugReportToast(before, sent, after, hasWebhook));

    [Fact]
    public Task Settings_PendingBugReportsText() => WpfHost.RunAsync(() =>
    {
        using var vm = new MainViewModel();
        Assert.False(vm.Settings.HasPendingBugReports);
        vm.Settings.SetPendingBugReports(3);
        Assert.True(vm.Settings.HasPendingBugReports);
        Assert.Equal("3 reportes de problemas pendientes de enviar", vm.Settings.PendingBugReportsText);
        vm.Settings.SetPendingBugReports(1, waitsForTeam: true);
        Assert.True(vm.Settings.PendingBugReportsWaitForTeam);
        Assert.Equal("1 reporte de problema pendiente: se enviará cuando el equipo active los envíos", vm.Settings.PendingBugReportsText);
        return Task.CompletedTask;
    });

    // ───────────── Perfiles de lanzamiento ─────────────

    [Fact]
    public Task LaunchArguments_AreTheProfileFollowedByTheUserArguments() => WithViewModel(vm =>
    {
        // Por defecto: solo lo que escriba el usuario (como antes de 1.0.5).
        vm.CurrentSettings.LaunchArguments = "-logFile log.txt";
        Assert.Equal("-logFile log.txt", vm.BuildLaunchArguments());

        // El desplegable de Ajustes muestra los perfiles con su nombre y cambia el ajuste.
        Assert.Equal(LaunchProfiles.All.Select(LaunchProfiles.GetDisplayName), vm.Settings.LaunchProfileOptions.Select(o => o.Label));
        Assert.Equal(LaunchProfile.Default, vm.Settings.SelectedLaunchProfile!.Value);
        vm.Settings.SelectedLaunchProfile = vm.Settings.LaunchProfileOptions.Single(o => o.Value == LaunchProfile.Windowed720);
        Assert.Equal(LaunchProfile.Windowed720, vm.CurrentSettings.LaunchProfile);

        // El perfil va delante y lo que escribe el usuario manda (su -screen-width sustituye al del perfil).
        vm.CurrentSettings.LaunchArguments = "-screen-width 1600 -logFile log.txt";
        Assert.Equal(LaunchProfiles.Combine(LaunchProfile.Windowed720, "-screen-width 1600 -logFile log.txt"), vm.BuildLaunchArguments());
        Assert.Equal("-screen-fullscreen 0 -screen-height 720 -screen-width 1600 -logFile log.txt", vm.BuildLaunchArguments());

        vm.Settings.SelectedLaunchProfile = vm.Settings.LaunchProfileOptions.Single(o => o.Value == LaunchProfile.Borderless);
        vm.CurrentSettings.LaunchArguments = "";
        Assert.Equal("-screen-fullscreen 1 -window-mode borderless", vm.BuildLaunchArguments());
        return Task.CompletedTask;
    });

    // ───────────── Modo desarrollador ─────────────

    [Fact]
    public Task DeveloperMode_ShowsTheTools_AndOpensAnExtraInstanceWithTheSameArguments() => WithViewModel(async vm =>
    {
        var calls = new List<(string Dir, string Exe, string? Args)>();
        vm.ExtraInstanceLauncher = (dir, exe, args) =>
        {
            calls.Add((dir, exe, args));
            return 4242;
        };

        Assert.False(vm.IsDeveloperMode);
        Assert.False(vm.LaunchExtraInstanceCommand.CanExecute(null));

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Settings.DeveloperMode = true;
        Assert.True(vm.CurrentSettings.DeveloperMode);
        Assert.True(vm.IsDeveloperMode);
        Assert.Contains(nameof(MainViewModel.IsDeveloperMode), changed); // el botón junto a JUGAR y la entrada del menú aparecen
        Assert.True(vm.LaunchExtraInstanceCommand.CanExecute(null));

        // Sin juego instalado: solo avisa.
        vm.LaunchExtraInstanceCommand.Execute(null);
        await WpfHost.WaitUntilAsync(() => vm.ToastText.StartsWith("Instala Coop2D primero", StringComparison.Ordinal), "aviso sin juego");
        Assert.Empty(calls);

        // Instalado: misma carpeta, exe y argumentos (perfil + usuario) que JUGAR, más su propio log (1.0.6: así sus
        // eventos no cuentan para las misiones de prueba).
        MainViewModelAccess.SetInstalled(vm, MainViewModelAccess.Installed("0.0.3", "0.0.3"));
        vm.CurrentSettings.LaunchProfile = LaunchProfile.Windowed1080;
        vm.CurrentSettings.LaunchArguments = "-nolog";
        vm.LaunchExtraInstanceCommand.Execute(null);
        await WpfHost.WaitUntilAsync(() => calls.Count == 1, "instancia extra");
        Assert.Equal(@"C:\Juegos\Coop2D\game", calls[0].Dir);
        Assert.Equal(LauncherConfig.GameExecutableName, calls[0].Exe);
        Assert.StartsWith(vm.BuildLaunchArguments() + " -logFile \"", calls[0].Args);
        Assert.Contains("extra-instance-logs", calls[0].Args, StringComparison.Ordinal);
        Assert.StartsWith("-screen-fullscreen 0 -screen-width 1920", calls[0].Args);
        await WpfHost.WaitUntilAsync(() => vm.ToastText.StartsWith("Se abrió otra instancia", StringComparison.Ordinal), "toast");
        Assert.False(vm.IsGameRunning); // la instancia extra no se sigue: JUGAR no cambia

        vm.Settings.DeveloperMode = false;
        Assert.False(vm.IsDeveloperMode);
        Assert.False(vm.LaunchExtraInstanceCommand.CanExecute(null));
    });

    // ───────────── Hostear partida ─────────────

    [Fact]
    public Task Hosting_ShowsTheLanIp_CopiesIt_AndAsksForThePublicIpOnlyWhenPressed() => WithViewModel(async vm =>
    {
        var copied = new List<string>();
        var clipboardBusy = false;
        var publicIpCalls = 0;
        vm.GetLocalAddresses = () => new[]
        {
            new LocalAddress("192.168.1.20", "Wi-Fi", IsPrivate: true),
            new LocalAddress("26.10.0.5", "Radmin VPN", IsPrivate: false),
        };
        vm.CopyText = text =>
        {
            if (clipboardBusy) return Task.FromResult(false);
            copied.Add(text);
            return Task.FromResult(true);
        };
        vm.GetPublicIp = _ =>
        {
            publicIpCalls++;
            return Task.FromResult<string?>(publicIpCalls == 1 ? null : "203.0.113.7");
        };
        vm.ApplyManifest(TestManifest.Make(m => m.Hosting = new HostingInfo { Port = 7770, Notes = "Abre el puerto UDP 7770 o usa Radmin VPN." }));

        vm.ShowHostingCommand.Execute(null);
        var dialog = await WaitForDialogAsync<HostingDialogViewModel>(vm);
        Assert.Equal("192.168.1.20", dialog.LanAddress!.Address);
        Assert.Equal("IP en tu red (LAN)", dialog.LanTitle);
        Assert.Equal("26.10.0.5", Assert.Single(dialog.OtherAddresses).Address);
        Assert.Equal("Puerto del juego: 7770", dialog.PortText);
        Assert.Equal("Abre el puerto UDP 7770 o usa Radmin VPN.", dialog.Notes);
        Assert.Equal(0, publicIpCalls); // nunca sin pedirlo
        Assert.False(dialog.HasPublicIp);

        await ((AsyncRelayCommand)dialog.LanAddress.CopyCommand).ExecuteAsync(null);
        Assert.Equal(new[] { "192.168.1.20" }, copied);
        Assert.Equal("Copiado: 192.168.1.20", dialog.CopyStatusText);
        Assert.False(dialog.IsCopyError);

        clipboardBusy = true; // otra aplicación tiene el portapapeles
        await ((AsyncRelayCommand)dialog.OtherAddresses[0].CopyCommand).ExecuteAsync(null);
        Assert.True(dialog.IsCopyError);
        Assert.Equal(HostingDialogViewModel.CopyFailedText, dialog.CopyStatusText);
        clipboardBusy = false;

        // IP pública: el primer intento falla (sin conexión), el segundo funciona y se puede copiar.
        await ((AsyncRelayCommand)dialog.ShowPublicIpCommand).ExecuteAsync(null);
        Assert.True(dialog.HasPublicIpError);
        Assert.False(dialog.HasPublicIp);
        await ((AsyncRelayCommand)dialog.ShowPublicIpCommand).ExecuteAsync(null);
        Assert.False(dialog.HasPublicIpError);
        Assert.Equal("203.0.113.7", dialog.PublicIp);
        Assert.Equal("Volver a consultar", dialog.ShowPublicIpText);
        await ((AsyncRelayCommand)dialog.CopyPublicIpCommand).ExecuteAsync(null);
        Assert.Equal("203.0.113.7", copied[^1]);

        dialog.Cancel(); // Esc
        Assert.True(await dialog.Completion);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog == null, "cierre del diálogo");
    });

    [Fact]
    public Task Hosting_WithoutNetwork_OrLanIp_SaysWhatThereIs() => WpfHost.RunAsync(() =>
    {
        var none = new HostingDialogViewModel(Array.Empty<LocalAddress>(), null, null, _ => Task.FromResult<string?>(null), _ => Task.FromResult(true));
        Assert.True(none.HasNoAddresses);
        Assert.False(none.HasLanAddress);
        Assert.False(none.HasPort);
        Assert.False(none.HasNotes);

        var publicOnly = new HostingDialogViewModel(new[] { new LocalAddress("26.10.0.5", "Radmin VPN", false) }, 0, "  ",
            _ => Task.FromResult<string?>(null), _ => Task.FromResult(true));
        Assert.False(publicOnly.HasNoAddresses);
        Assert.False(publicOnly.HasLanAddress);
        Assert.Equal("Direcciones de este PC", publicOnly.OtherAddressesTitle);
        Assert.False(publicOnly.HasPort); // 0 no es un puerto
        Assert.False(publicOnly.HasNotes);
        return Task.CompletedTask;
    });
}
