using System.Globalization;
using System.IO;
using PalitoLauncher.Core.Missions;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>
/// Misiones de prueba en el MainViewModel (sin InitializeAsync: nunca consulta GitHub ni Discord): tarjeta, eventos del
/// juego leídos de un Player.log de prueba, marcas del jugador, aviso de versión y argumentos del juego.
/// </summary>
public sealed class MissionsUiTests
{
    private static Task WithViewModel(Func<MainViewModel, TempDir, Task> test) => WpfHost.RunAsync(async () =>
    {
        using var temp = new TempDir();
        using var vm = new MainViewModel();
        vm.UtcNow = () => TestManifest.Now;
        await test(vm, temp);
    });

    /// <summary>Player.log con una sesión que arranca una hora antes de TestManifest.Now.</summary>
    private static string WriteLog(TempDir temp, string session, params string[] events)
    {
        var lines = new List<string>();
        var start = TestManifest.Now.AddHours(-1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        lines.Add($"[PalitoEvent] v=1 seq=1 t=0.500 ev=session_start game_version=0.0.4 dev_build=true launcher_session={session} utc={start}");
        for (var i = 0; i < events.Length; i++)
            lines.Add($"[PalitoEvent] v=1 seq={i + 2} t={(i + 2).ToString(CultureInfo.InvariantCulture)}.000 {events[i]}");
        var path = Path.Combine(temp.Path, "Player.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static void UseLog(MainViewModel vm, string? path)
        => vm.MissionLogPathsOverride = () => Task.FromResult<(string?, string?)>((path, null));

    [Fact]
    public Task Card_ShowsTheSetAndHidesWithTheSetting() => WithViewModel((vm, _) =>
    {
        var (set, state) = TestMissions.Sample();
        vm.ApplyMissionSet(set, state);

        Assert.True(vm.Missions.IsVisible);
        Assert.Equal("Misiones del Pre 2", vm.Missions.Title);
        Assert.Equal("1 de 4 completadas", vm.Missions.ProgressText);
        Assert.Equal(25, vm.Missions.ProgressValue);
        Assert.Equal(new[] { "vapor", "tarde", "kills", "numeros" }, vm.Missions.Items.Select(i => i.Id));
        Assert.True(vm.Missions.Items[0].IsDone);
        Assert.True(vm.Missions.Items[0].ShowCheck);
        Assert.Equal("Progreso: 12 / 30", vm.Missions.Items[2].StatusText);
        Assert.True(vm.Missions.Items[3].ShowWorked);
        Assert.False(vm.Missions.Items[1].ShowWorked); // las automáticas no se marcan como hechas a mano

        vm.Settings.ShowMissions = false;
        Assert.False(vm.Missions.IsVisible);
        vm.Settings.ShowMissions = true;
        Assert.True(vm.Missions.IsVisible);

        vm.ApplyMissionSet(null, null);
        Assert.False(vm.Missions.IsVisible);
        Assert.Empty(vm.Missions.Items);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Card_RespectsTheWindowOfTheSet() => WithViewModel((vm, _) =>
    {
        var set = MissionsProvider.Parse("""
            { "id": "futuro", "startsUtc": "2026-10-30T00:00:00Z", "missions": [ { "id": "a", "title": "A", "manual": true } ] }
            """)!;
        vm.ApplyMissionSet(set, new MissionSetState { SetId = set.Id, CreatedUtc = TestManifest.Now });
        Assert.False(vm.Missions.IsVisible);

        vm.UtcNow = () => new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        vm.OnManifestClockTick();
        Assert.True(vm.Missions.IsVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Logs_CompleteMissionsOnceAndShowAToast() => WithViewModel(async (vm, temp) =>
    {
        var (set, state) = TestMissions.Sample();
        vm.ApplyMissionSet(set, state);
        UseLog(vm, WriteLog(temp, "partida-1",
            "ev=match_joined role=client slot=2 players=3 match_time=95 late=true",
            "ev=enemy_killed enemy=chaser by=me source=basic",
            "ev=enemy_killed enemy=chaser by=ally source=basic",
            "ev=enemy_killed enemy=chaser by=me source=reaction",
            "ev=match_left reason=quit duration=200"));

        await vm.ProcessMissionLogsAsync(afterGame: true);

        Assert.True(vm.Missions.Items[1].IsDone);
        Assert.StartsWith("Completada", vm.Missions.Items[1].StatusText, StringComparison.Ordinal);
        Assert.Equal("Progreso: 14 / 30", vm.Missions.Items[2].StatusText); // 12 + 2 «by=me»
        Assert.Equal("✓ Misión completada: Únete a una partida ya empezada", vm.ToastText);
        Assert.Equal("2 de 4 completadas", vm.Missions.ProgressText);
        Assert.True(state.Dirty);

        // Releer el mismo log (p.ej. al volver a abrir el launcher) no suma otra vez.
        await vm.ProcessMissionLogsAsync(afterGame: false);
        Assert.Equal("Progreso: 14 / 30", vm.Missions.Items[2].StatusText);
        Assert.Equal(new[] { "partida-1" }, state.ProcessedSessions);
    });

    [Fact]
    public Task Logs_WithoutEventsOrWithoutLogDoNothing() => WithViewModel(async (vm, temp) =>
    {
        var (set, state) = TestMissions.Sample();
        vm.ApplyMissionSet(set, state);

        UseLog(vm, null);
        await vm.ProcessMissionLogsAsync(afterGame: true);

        var path = Path.Combine(temp.Path, "Player.log");
        File.WriteAllLines(path, new[] { "Initialize engine version: 6000.0.1f1", "Mono path[0] = ..." }); // build sin eventos
        UseLog(vm, path);
        await vm.ProcessMissionLogsAsync(afterGame: true);

        Assert.Empty(state.ProcessedSessions);
        Assert.Equal("1 de 4 completadas", vm.Missions.ProgressText);
    });

    [Fact]
    public Task Marks_ManualFailSkipUndoAndCheck() => WithViewModel((vm, _) =>
    {
        var (set, state) = TestMissions.Sample();
        vm.ApplyMissionSet(set, state);
        var vapor = vm.Missions.Items[0];
        var tarde = vm.Missions.Items[1];
        var manual = vm.Missions.Items[3];

        manual.WorkedCommand.Execute(null);
        Assert.Equal(MissionState.Done, state.Missions["numeros"].State);
        Assert.False(state.Missions["numeros"].Automatic);
        Assert.StartsWith("Funcionó", manual.StatusText, StringComparison.Ordinal);
        Assert.True(manual.ShowUndo);
        manual.UndoCommand.Execute(null);
        Assert.Equal(MissionState.Pending, state.Missions["numeros"].State);

        tarde.FailedCommand.Execute(null);
        Assert.True(tarde.IsEditingNote);
        Assert.Equal("¿Qué falló? (opcional)", tarde.NoteLabel);
        tarde.NoteDraft = "  El host no me veía  ";
        tarde.SaveNoteCommand.Execute(null);
        Assert.False(tarde.IsEditingNote);
        Assert.Equal(MissionState.Failed, state.Missions["tarde"].State);
        Assert.Equal("El host no me veía", tarde.Note);
        Assert.True(tarde.IsFailed);

        manual.SkipCommand.Execute(null);
        Assert.Equal(MissionState.Skipped, state.Missions["numeros"].State);
        Assert.Equal("No la probaste", manual.StatusText);

        // Completada sola: no se puede deshacer, pero sí responder a la comprobación.
        Assert.False(vapor.ShowUndo);
        vapor.CheckNoCommand.Execute(null);
        Assert.Equal("¿Qué se vio mal? (opcional)", vapor.NoteLabel);
        vapor.NoteDraft = "El titular salió detrás de la nube";
        vapor.SaveNoteCommand.Execute(null);
        Assert.Equal(MissionState.Done, state.Missions["vapor"].State);
        Assert.False(state.Missions["vapor"].CheckAnswer);
        Assert.False(vapor.ShowCheck);
        Assert.Contains("se vio mal", vapor.StatusText, StringComparison.Ordinal);

        Assert.True(state.Dirty);
        return Task.CompletedTask;
    });

    [Fact]
    public Task VersionWarning_WhenTheInstalledGameIsOlder() => WithViewModel((vm, _) =>
    {
        var set = MissionsProvider.Parse("""
            { "id": "s", "minGameVersion": "0.0.4", "missions": [ { "id": "a", "title": "A", "manual": true } ] }
            """)!;
        MainViewModelAccess.SetInstalled(vm, MainViewModelAccess.Installed("0.0.3", "0.0.3"));
        vm.ApplyMissionSet(set, new MissionSetState { SetId = set.Id, CreatedUtc = TestManifest.Now });
        Assert.True(vm.Missions.HasVersionWarning);
        Assert.Contains("0.0.3", vm.Missions.VersionWarning, StringComparison.Ordinal);

        MainViewModelAccess.SetInstalled(vm, MainViewModelAccess.Installed("0.0.4", "0.0.4"));
        vm.ApplyMissionSet(set, new MissionSetState { SetId = set.Id, CreatedUtc = TestManifest.Now });
        Assert.False(vm.Missions.HasVersionWarning);
        return Task.CompletedTask;
    });

    [Fact]
    public Task LaunchArguments_AddTheSessionAndTheExtraInstanceLog() => WpfHost.RunAsync(() =>
    {
        var withUser = MainViewModel.AppendMissionSession("-screen-fullscreen 0 ", out var id);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal($"-screen-fullscreen 0 -palitoSession {id}", withUser);
        var alone = MainViewModel.AppendMissionSession("", out var other);
        Assert.Equal($"-palitoSession {other}", alone);
        Assert.NotEqual(id, other); // una por ejecución

        var extra = MainViewModel.AppendExtraInstanceLog("-screen-width 1280");
        Assert.StartsWith("-screen-width 1280 -logFile \"", extra, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(MainViewModel.ExtraInstanceLogsDir, "game-extra-"), extra, StringComparison.Ordinal);
        Assert.EndsWith(".log\"", extra, StringComparison.Ordinal);
        Assert.Equal("-logFile mio.txt", MainViewModel.AppendExtraInstanceLog("-logFile mio.txt")); // el del jugador manda
        return Task.CompletedTask;
    });

    [Fact]
    public void Toast_ListsUpToThreeMissions()
    {
        var set = MissionsProvider.Parse("""
            { "id": "s", "missions": [
              { "id": "a", "title": "A", "manual": true }, { "id": "b", "title": "B", "manual": true },
              { "id": "c", "title": "C", "manual": true }, { "id": "d", "title": "D", "manual": true } ] }
            """)!;
        Assert.Equal("✓ 4 misiones completadas: A, B, C…", MainViewModel.MissionsCompletedToast(set.Missions));
        Assert.Equal("✓ Misión completada: A", MainViewModel.MissionsCompletedToast(set.Missions.Take(1).ToList()));
    }
}
