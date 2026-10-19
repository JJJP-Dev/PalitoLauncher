using System.IO;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Remote;
using PalitoLauncher.Helpers;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// launcher.json en el MainViewModel (sin InitializeAsync: nunca consulta GitHub, y con el reloj fijo de TestManifest):
/// anuncios (orden, cerrar, críticos fijos, ventana de tiempo), builds con problemas (aviso, badge, confirmaciones),
/// enlaces de comunidad, tarjeta de la sesión de prueba, aviso de Windows y «Qué ha cambiado desde tu versión».
/// </summary>
public sealed class ManifestUiTests
{
    private static readonly DateTimeOffset Now = TestManifest.Now;

    private static Task WithViewModel(Func<MainViewModel, Task> test) => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        vm.UtcNow = () => Now;
        await test(vm);
    });

    private static Task WithViewModel(Action<MainViewModel> test) => WithViewModel(vm =>
    {
        test(vm);
        return Task.CompletedTask;
    });

    private static InstallStatus Installed(string version) => MainViewModelAccess.Installed(version, version);

    // ───────────── Anuncios ─────────────

    [Fact]
    public Task Announcements_CriticalFirst_InfoCanBeClosed_CriticalCannot() => WithViewModel(vm =>
    {
        vm.ApplyManifest(TestManifest.Make(m =>
        {
            m.Announcements.Add(new Announcement { Id = "info", Title = "Bienvenida", Message = "Hola", Url = "https://discord.gg/abc", LinkText = "Discord" });
            m.Announcements.Add(new Announcement { Id = "warn", Level = AnnouncementLevel.Warning, Title = "Mantenimiento" });
            m.Announcements.Add(new Announcement { Id = "crit", Level = AnnouncementLevel.Critical, Title = "No juguéis la 0.0.4", Dismissible = true });
        }));

        Assert.True(vm.HasAnnouncements);
        Assert.Equal(new[] { "crit", "warn", "info" }, vm.Announcements.Select(a => a.Id));
        Assert.Equal(new[] { NoticeKind.Error, NoticeKind.Warning, NoticeKind.Info }, vm.Announcements.Select(a => a.Kind));

        var critical = vm.Announcements[0];
        Assert.False(critical.IsDismissible);
        Assert.False(critical.DismissCommand.CanExecute(null));

        var info = vm.Announcements[2];
        Assert.True(info.HasAction);
        Assert.Equal("Discord", info.ActionText);
        Assert.Equal("Hola", info.Message);
        Assert.True(info.IsDismissible);

        info.DismissCommand.Execute(null);
        Assert.Equal(new[] { "crit", "warn" }, vm.Announcements.Select(a => a.Id));
        Assert.Contains("info", vm.CurrentSettings.DismissedAnnouncementIds);

        // Un crítico no se cierra...
        vm.DismissAnnouncement("crit");
        Assert.DoesNotContain("crit", vm.CurrentSettings.DismissedAnnouncementIds);
        Assert.Contains(vm.Announcements, a => a.Id == "crit");

        // ...ni aunque su Id esté entre los cerrados (p.ej. settings.json editado a mano).
        vm.CurrentSettings.DismissedAnnouncementIds.Add("crit");
        vm.OnManifestClockTick();
        Assert.Contains(vm.Announcements, a => a.Id == "crit");
    });

    [Fact]
    public Task Announcements_FollowTheirTimeWindow_WhenTheClockMoves() => WithViewModel(vm =>
    {
        vm.ApplyManifest(TestManifest.Make(m => m.Announcements.Add(new Announcement
        {
            Id = "sabado", Title = "Sesión este sábado", StartsUtc = Now.AddHours(1), EndsUtc = Now.AddHours(3),
        })));
        Assert.False(vm.HasAnnouncements);

        vm.UtcNow = () => Now.AddHours(2);
        vm.OnManifestClockTick();
        Assert.Equal("Sesión este sábado", Assert.Single(vm.Announcements).Title);

        vm.UtcNow = () => Now.AddHours(4);
        vm.OnManifestClockTick();
        Assert.False(vm.HasAnnouncements);
        Assert.Empty(vm.Announcements);
    });

    [Fact]
    public Task Announcements_LinkButton_OnlyForHttps_WithDefaultText() => WithViewModel(vm =>
    {
        // Sin sanear a propósito: la UI tampoco abre enlaces que no sean https.
        var manifest = new LauncherManifest();
        manifest.Announcements.Add(new Announcement { Id = "a", Title = "Con enlace", Url = "https://example.com/notas" });
        manifest.Announcements.Add(new Announcement { Id = "b", Title = "Enlace inseguro", Url = "http://example.com" });
        vm.ApplyManifest(manifest);

        var withLink = vm.Announcements.Single(a => a.Id == "a");
        Assert.Equal("Más información", withLink.ActionText);
        Assert.True(withLink.ActionCommand.CanExecute(null));
        var insecure = vm.Announcements.Single(a => a.Id == "b");
        Assert.False(insecure.HasAction);
        Assert.False(insecure.ActionCommand.CanExecute(null));
    });

    [Fact]
    public Task WithoutManifest_NothingNewIsShown() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));
        vm.ApplyManifest(new LauncherManifest());

        Assert.False(vm.HasAnnouncements);
        Assert.False(vm.HasCommunityLinks);
        Assert.False(vm.HasPlaytest);
        Assert.False(vm.Playtest.IsVisible);
        Assert.All(vm.Releases, r => Assert.False(r.IsBlocked));
    });

    // ───────────── Builds con problemas ─────────────

    [Fact]
    public Task BlockedInstalledBuild_ShowsAFixedCriticalBanner_WithUpdate() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());
        vm.ApplyManifest(TestManifest.Make(m => m.BlockedBuilds.Add(new BlockedBuild { Match = "v0.0.2", Reason = TestManifest.BossReason })));
        Assert.False(vm.HasAnnouncements); // aún no hay nada instalado

        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));

        var banner = Assert.Single(vm.Announcements);
        Assert.Equal($"Tu versión (0.0.2) tiene un problema: {TestManifest.BossReason}", banner.Title);
        Assert.Equal(NoticeKind.Error, banner.Kind);
        Assert.False(banner.IsDismissible);
        Assert.Equal("Actualizar", banner.ActionText);
        Assert.Equal("Actualiza a 0.0.3 para evitarlo.", banner.Message);

        // Versiones: badge rojo solo en la que tiene el problema.
        var blocked = vm.Releases.Single(r => r.VersionText == "0.0.2");
        Assert.True(blocked.IsBlocked);
        Assert.Equal(TestManifest.BossReason, blocked.BlockedReason);
        Assert.Equal($"Problema conocido: {TestManifest.BossReason}", blocked.BlockedText);
        Assert.False(vm.Releases.Single(r => r.VersionText == "0.0.3").IsBlocked);

        // Solo avisa: el estado sigue siendo «hay actualización».
        Assert.Equal(Core.State.LauncherState.UpdateAvailable, vm.State);
    });

    [Fact]
    public Task BlockedInstalledBuild_WithoutAGoodNewerOne_OffersVersions() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));
        vm.ApplyManifest(TestManifest.Make(m =>
        {
            m.BlockedBuilds.Add(new BlockedBuild { Match = "0.0.2", Reason = TestManifest.BossReason });
            m.BlockedBuilds.Add(new BlockedBuild { Match = "2D_0.0.3.zip", Reason = "Tampoco va" });
        }));

        var banner = Assert.Single(vm.Announcements);
        Assert.Equal("Ver versiones", banner.ActionText);
        banner.ActionCommand.Execute(null);
        Assert.True(vm.IsVersionsPage);
    });

    [Fact]
    public Task Play_WithABlockedBuild_AsksFirst() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));
        vm.ApplyManifest(TestManifest.Make(m => m.BlockedBuilds.Add(new BlockedBuild { Match = "v0.0.2", Reason = TestManifest.BossReason })));

        var launch = MainViewModelAccess.LaunchGameAsync(vm);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog is MessageDialogViewModel, "pregunta de la versión con problemas");
        var dialog = (MessageDialogViewModel)vm.Dialogs.CurrentDialog!;
        Assert.True(dialog.IsDanger);
        Assert.Equal("Jugar igualmente", dialog.PrimaryText);
        Assert.Contains($"Esta versión tiene un problema conocido: {TestManifest.BossReason}", dialog.Message);
        Assert.Contains("0.0.3", dialog.Message); // recomienda actualizar
        Assert.EndsWith("¿Jugar igualmente?", dialog.Message);

        dialog.Cancel();
        await launch;
        Assert.False(vm.IsGameRunning);
        Assert.Null(vm.Dialogs.CurrentDialog);
    });

    [Fact]
    public Task InstallFromVersions_ABlockedBuild_AsksBeforeChoosingAFolder() => WithViewModel(async vm =>
    {
        var releases = TestManifest.Releases();
        await MainViewModelAccess.ApplyReleasesAsync(vm, releases);
        MainViewModelAccess.Initialized(vm) = true;
        vm.ApplyManifest(TestManifest.Make(m => m.BlockedBuilds.Add(new BlockedBuild { Match = "v0.0.2", Reason = TestManifest.BossReason })));

        var install = MainViewModelAccess.InstallSpecificReleaseAsync(vm, releases[1]);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog is MessageDialogViewModel, "pregunta de la versión con problemas");
        var dialog = (MessageDialogViewModel)vm.Dialogs.CurrentDialog!;
        Assert.True(dialog.IsDanger);
        Assert.Equal("Versión con problemas", dialog.Title);
        Assert.Contains($"0.0.2 tiene un problema conocido: {TestManifest.BossReason}", dialog.Message);
        Assert.Equal("Instalar igualmente", dialog.PrimaryText);

        dialog.Cancel();
        await install;
        Assert.Null(vm.Dialogs.CurrentDialog); // no se abrió el diálogo de instalación
    });

    [Fact]
    public Task InstallFromVersions_ReplacingTheInstalled_WarnsInTheSameConfirmation() => WithViewModel(async vm =>
    {
        var releases = TestManifest.Releases();
        await MainViewModelAccess.ApplyReleasesAsync(vm, releases);
        MainViewModelAccess.Initialized(vm) = true;
        MainViewModelAccess.InstallRoot(vm) = @"C:\Juegos\Coop2D";
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.1"));
        vm.ApplyManifest(TestManifest.Make(m => m.BlockedBuilds.Add(new BlockedBuild { Match = "v0.0.2", Reason = TestManifest.BossReason })));

        var install = MainViewModelAccess.InstallSpecificReleaseAsync(vm, releases[1]);
        await WpfHost.WaitUntilAsync(() => vm.Dialogs.CurrentDialog is MessageDialogViewModel, "confirmación de instalar");
        var dialog = (MessageDialogViewModel)vm.Dialogs.CurrentDialog!;
        Assert.Equal("Instalar 0.0.2", dialog.Title);
        Assert.True(dialog.IsDanger);
        Assert.Equal("Instalar igualmente", dialog.PrimaryText);
        Assert.Contains("Se reemplazará la versión instalada (0.0.1) por 0.0.2", dialog.Message);
        Assert.Contains($"problema conocido: {TestManifest.BossReason}", dialog.Message);

        dialog.Cancel();
        await install;
        Assert.Null(vm.Dialogs.CurrentDialog);
    });

    // ───────────── Enlaces de comunidad ─────────────

    [Fact]
    public Task CommunityLinks_OnlyHttps_WithTheirIcons() => WithViewModel(vm =>
    {
        var manifest = new LauncherManifest(); // sin sanear: la UI también descarta lo que no es https
        manifest.Links.Add(new CommunityLink { Label = "Discord", Url = "https://discord.gg/coop2d", Icon = "discord" });
        manifest.Links.Add(new CommunityLink { Label = "Inseguro", Url = "http://example.com", Icon = "web" });
        manifest.Links.Add(new CommunityLink { Label = "Steam", Url = "https://store.steampowered.com/app/1", Icon = "steam" });
        manifest.Links.Add(new CommunityLink { Label = "Web", Url = "https://palito.games", Icon = "algo-raro" });
        vm.ApplyManifest(manifest);

        Assert.True(vm.HasCommunityLinks);
        Assert.Equal(new[] { "Discord", "Steam", "Web" }, vm.CommunityLinks.Select(l => l.Label));
        Assert.Equal(new[] { "\uE8BD", "\uE7FC", "\uE774" }, vm.CommunityLinks.Select(l => l.Glyph));
        Assert.Equal("Abrir discord.gg", vm.CommunityLinks[0].ToolTip);
        Assert.All(vm.CommunityLinks, l => Assert.True(l.OpenCommand.CanExecute(null)));
    });

    [Theory]
    [InlineData("discord", "\uE8BD")]
    [InlineData("itch", "\uE774")]
    [InlineData("web", "\uE774")]
    [InlineData("steam", "\uE7FC")]
    [InlineData("youtube", "\uE714")]
    [InlineData("GitHub", "\uE943")]
    [InlineData(null, "\uE774")]
    public void CommunityLink_IconToGlyph(string? icon, string glyph) => Assert.Equal(glyph, CommunityLinkViewModel.GlyphFor(icon));

    // ───────────── Sesiones de prueba ─────────────

    [Fact]
    public Task PlaytestCard_Upcoming_WithCountdown_AndUpdateAction() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));
        var session = TestManifest.Session(TimeSpan.FromMinutes(150), requiredVersion: "v0.0.3");
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(session)));

        var card = vm.Playtest;
        Assert.True(vm.HasPlaytest);
        Assert.True(card.IsVisible);
        Assert.Equal("Próxima sesión de prueba", card.HeaderText);
        Assert.Equal("Prueba del Pre 2", card.Title);
        Assert.Equal(UiFormat.SessionWhen(session.StartsUtc, session.StartsUtc.AddMinutes(120)), card.WhenText);
        Assert.Equal("Empieza en 2 h 30 min", card.CountdownText);
        Assert.False(card.IsOngoing);
        Assert.False(card.IsSoon);
        Assert.Equal("Probamos el jefe final en cooperativo.", card.Description);
        Assert.True(card.HasDiscord);

        Assert.True(card.HasVersionInfo);
        Assert.False(card.IsVersionOk);
        Assert.Equal("Necesitas 0.0.3", card.VersionText);
        Assert.Equal("Actualizar", card.VersionActionText);
        Assert.False(card.HasVersionHint);

        // La cuenta atrás avanza con el reloj (cada 30 s en la app).
        vm.UtcNow = () => Now.AddMinutes(140);
        vm.OnManifestClockTick();
        Assert.Equal("Empieza en 10 min", card.CountdownText);
        Assert.True(card.IsSoon);
    });

    [Fact]
    public Task PlaytestCard_VersionStates() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromDays(1), requiredVersion: "v0.0.2"))));
        var card = vm.Playtest;

        // Nada instalado: «Instalar esa versión».
        Assert.Equal("Necesitas 0.0.2", card.VersionText);
        Assert.Equal("Instalar esa versión", card.VersionActionText);

        // Instalada una más nueva (no es una actualización): también «Instalar esa versión».
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.3"));
        Assert.False(card.IsVersionOk);
        Assert.Equal("Instalar esa versión", card.VersionActionText);

        // Instalada la correcta.
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));
        Assert.True(card.IsVersionOk);
        Assert.Equal("Tienes la versión correcta", card.VersionText);
        Assert.False(card.HasVersionAction);

        // Una versión que no está en la lista: sin botón y con ayuda.
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromDays(1), requiredVersion: "v0.0.9"))));
        Assert.False(card.IsVersionOk);
        Assert.Equal("Necesitas v0.0.9", card.VersionText);
        Assert.False(card.HasVersionAction);
        Assert.True(card.HasVersionHint);

        // Sin versión pedida: no se muestra nada de versiones.
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromDays(1)))));
        Assert.False(card.HasVersionInfo);
    });

    [Fact]
    public Task PlaytestCard_Ongoing_ShowsWhenItEnds() => WithViewModel(vm =>
    {
        var session = TestManifest.Session(TimeSpan.FromMinutes(-30));
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(session)));

        var card = vm.Playtest;
        Assert.True(card.IsOngoing);
        Assert.True(card.IsSoon);
        Assert.Equal("Sesión de prueba en curso", card.HeaderText);
        Assert.Equal($"En curso · termina a las {UiFormat.Time(session.StartsUtc.AddMinutes(120))}", card.CountdownText);

        // Al terminar desaparece.
        vm.UtcNow = () => Now.AddHours(3);
        vm.OnManifestClockTick();
        Assert.False(card.IsVisible);
        Assert.False(vm.HasPlaytest);
    });

    [Fact]
    public Task PlaytestReminder_FiresOnce_AndIsRemembered() => WithViewModel(vm =>
    {
        var reminders = new List<PlaytestReminder>();
        vm.PlaytestReminderRequested += (_, r) => reminders.Add(r);

        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromMinutes(10)))));

        var reminder = Assert.Single(reminders);
        Assert.Equal("Sesión de prueba en 10 min", reminder.Title);
        Assert.Contains("«Prueba del Pre 2» empieza a las", reminder.Message);
        Assert.Contains("pre2", vm.CurrentSettings.RemindedPlaytestIds);
        Assert.True(vm.IsToastVisible);

        vm.UtcNow = () => Now.AddMinutes(1);
        vm.OnManifestClockTick();
        vm.OnManifestClockTick();
        Assert.Single(reminders);
    });

    [Fact]
    public Task PlaytestReminder_NotTooEarly_AndRespectsTheSetting() => WithViewModel(vm =>
    {
        var reminders = new List<PlaytestReminder>();
        vm.PlaytestReminderRequested += (_, r) => reminders.Add(r);

        // Faltan 20 min: aún no.
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromMinutes(20)))));
        Assert.Empty(reminders);

        // Desactivado en Ajustes: no avisa aunque toque.
        vm.CurrentSettings.PlaytestRemindersEnabled = false;
        vm.UtcNow = () => Now.AddMinutes(10);
        vm.OnManifestClockTick();
        Assert.Empty(reminders);

        // Al activarlo con la sesión a punto de empezar, avisa ya.
        vm.Settings.PlaytestRemindersEnabled = true;
        Assert.True(vm.CurrentSettings.PlaytestRemindersEnabled);
        Assert.Single(reminders);
    });

    [Fact]
    public Task PlaytestReminder_JustStarted_SaysItHasBegun() => WithViewModel(vm =>
    {
        var reminders = new List<PlaytestReminder>();
        vm.PlaytestReminderRequested += (_, r) => reminders.Add(r);

        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromMinutes(-2)))));

        Assert.Equal("¡La sesión de prueba ha empezado!", Assert.Single(reminders).Title);
    });

    [Fact]
    public Task AddToCalendar_SavesTheIcsWhereTheUserChose() => WithViewModel(async vm =>
    {
        using var temp = new TempDir();
        string? proposed = null;
        string? revealed = null;
        var target = Path.Combine(temp.Path, "sesion.ics");
        vm.PickIcsPath = (fileName, _) =>
        {
            proposed = fileName;
            return target;
        };
        vm.RevealFile = path => revealed = path;
        vm.ApplyManifest(TestManifest.Make(m => m.Playtests.Add(TestManifest.Session(TimeSpan.FromDays(1), requiredVersion: "v0.0.3"))));

        vm.Playtest.AddToCalendarCommand.Execute(null);
        await WpfHost.WaitUntilAsync(() => revealed != null, "guardar el .ics");

        Assert.Equal("Coop2D - Prueba del Pre 2.ics", proposed);
        Assert.Equal(target, revealed);
        var ics = await File.ReadAllTextAsync(target);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.Contains("SUMMARY:Coop2D: Prueba del Pre 2", ics);
    });

    [Theory]
    [InlineData("Coop2D - Prueba: jefe/final?", "Coop2D - Prueba- jefe-final-")]
    [InlineData("  Coop2D - Sesión.  ", "Coop2D - Sesión")]
    [InlineData("???", "---")]
    [InlineData("", "Coop2D")]
    public void SafeFileName_RemovesWhatWindowsDoesNotAllow(string name, string expected)
        => Assert.Equal(expected, MainViewModel.SafeFileName(name));

    [Fact]
    public void SessionWhen_IsLocalAndInSpanish()
    {
        static DateTimeOffset Local(int year, int month, int day, int hour)
        {
            var local = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        var start = Local(2026, 10, 24, 21);
        Assert.Equal("sábado 24 oct · 21:00 – 23:00", UiFormat.SessionWhen(start, start.AddHours(2)));

        var late = Local(2026, 11, 14, 23);
        Assert.Equal("sábado 14 nov · 23:00 – 01:00 (domingo)", UiFormat.SessionWhen(late, late.AddHours(2)));
        Assert.Equal("miércoles 4 nov", UiFormat.DayAndDate(Local(2026, 11, 4, 12)));
    }

    // ───────────── «Qué ha cambiado desde tu versión» ─────────────

    [Fact]
    public Task WhatsChanged_WhenSkippingTwoOrMoreVersions() => WithViewModel(async vm =>
    {
        await MainViewModelAccess.ApplyReleasesAsync(vm, TestManifest.Releases());

        MainViewModelAccess.SetInstalled(vm, Installed("0.0.1"));
        Assert.True(vm.IsMultiVersionNotes);
        Assert.Equal("Qué ha cambiado desde tu versión (0.0.1 → 0.0.3)", vm.NotesTitle);
        Assert.Equal(new[] { "0.0.3", "0.0.2" }, vm.ReleaseNotesSections.Select(s => s.Title));
        Assert.False(vm.ReleaseNotesSections[0].ShowSeparator);
        Assert.True(vm.ReleaseNotesSections[1].ShowSeparator);
        Assert.All(vm.ReleaseNotesSections, s => Assert.True(s.HasDate));
        Assert.Contains("Segunda", FlowDocs.Text(vm.ReleaseNotesSections[1].Document));

        // Solo una versión por detrás: la tarjeta de siempre.
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.2"));
        Assert.False(vm.IsMultiVersionNotes);
        Assert.StartsWith("Novedades de 0.0.3", vm.NotesTitle);

        // Al día, o sin instalar: igual.
        MainViewModelAccess.SetInstalled(vm, Installed("0.0.3"));
        Assert.False(vm.IsMultiVersionNotes);
        MainViewModelAccess.SetInstalled(vm, new InstallStatus { State = InstallState.NotInstalled });
        Assert.False(vm.IsMultiVersionNotes);
        Assert.StartsWith("Novedades de 0.0.3", vm.NotesTitle);
    });
}
