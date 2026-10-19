using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Settings;
using PalitoLauncher.Tests.TestHelpers.C;

namespace PalitoLauncher.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly string _file;

    public SettingsStoreTests()
    {
        _file = _temp.Combine("settings.json");
    }

    public void Dispose() => _temp.Dispose();

    private string[] CorruptCopies() => Directory.GetFiles(_temp.Path, "settings.corrupt-*.json");

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var settings = new SettingsStore(_file).Load();

        Assert.Null(settings.InstallRoot);
        Assert.True(settings.CheckLauncherUpdates);
        Assert.Equal(AfterLaunchBehavior.Minimize, settings.AfterLaunch);
        Assert.Equal("", settings.LaunchArguments);
        Assert.Empty(CorruptCopies());
    }

    [Fact]
    public void SaveAndLoad_RoundTripsAllValues()
    {
        var store = new SettingsStore(_file);
        var original = new LauncherSettings
        {
            InstallRoot = @"D:\Juegos\Coop2D",
            IncludePrereleases = true,
            AutoDownloadUpdates = true,
            AutoUpdatePausedForTag = "0.0.2-Prototype-2",
            CheckLauncherUpdates = false,
            AfterLaunch = AfterLaunchBehavior.Close,
            LaunchArguments = "-screen-fullscreen 0 -screen-width 1280",
            MaxDownloadSpeedKBps = 2048,
            KeepDownloadedArchives = true,
            LastSeenReleaseTag = "0.0.1-Protype1",
            SkippedLauncherVersion = "1.1.0",
        };

        Assert.True(store.Save(original));
        var loaded = new SettingsStore(_file).Load();

        Assert.Equal(original.InstallRoot, loaded.InstallRoot);
        Assert.Equal(original.IncludePrereleases, loaded.IncludePrereleases);
        Assert.Equal(original.AutoDownloadUpdates, loaded.AutoDownloadUpdates);
        Assert.Equal(original.AutoUpdatePausedForTag, loaded.AutoUpdatePausedForTag);
        Assert.Equal(original.CheckLauncherUpdates, loaded.CheckLauncherUpdates);
        Assert.Equal(original.AfterLaunch, loaded.AfterLaunch);
        Assert.Equal(original.LaunchArguments, loaded.LaunchArguments);
        Assert.Equal(original.MaxDownloadSpeedKBps, loaded.MaxDownloadSpeedKBps);
        Assert.Equal(original.KeepDownloadedArchives, loaded.KeepDownloadedArchives);
        Assert.Equal(original.LastSeenReleaseTag, loaded.LastSeenReleaseTag);
        Assert.Equal(original.SkippedLauncherVersion, loaded.SkippedLauncherVersion);

        var json = File.ReadAllText(_file);
        Assert.Contains("\"Close\"", json, StringComparison.Ordinal);   // enums como texto
        Assert.Contains(Environment.NewLine + "  ", json, StringComparison.Ordinal); // indentado
        Assert.True(File.Exists(_file + ".bak"), "un Load correcto crea la copia de seguridad");
        Assert.Equal(json, File.ReadAllText(_file + ".bak"));
    }

    [Fact]
    public void Save_DoesNotModifyCallerInstance()
    {
        var settings = new LauncherSettings { LaunchArguments = null! };

        Assert.True(new SettingsStore(_file).Save(settings));

        Assert.Null(settings.LaunchArguments);
        Assert.Equal("", new SettingsStore(_file).Load().LaunchArguments);
    }

    [Fact]
    public void Load_EmptyFile_UsesBackup()
    {
        File.WriteAllText(_file + ".bak", """{ "InstallRoot": "E:\\Juegos\\Coop2D", "AutoDownloadUpdates": true }""");
        File.WriteAllText(_file, "");

        var settings = new SettingsStore(_file).Load();

        Assert.Equal(@"E:\Juegos\Coop2D", settings.InstallRoot);
        Assert.True(settings.AutoDownloadUpdates);
    }

    [Fact]
    public void Load_ZeroFilledFile_UsesBackup()
    {
        File.WriteAllText(_file + ".bak", """{ "InstallRoot": "E:\\Juegos\\Coop2D" }""");
        File.WriteAllBytes(_file, new byte[64]);

        Assert.Equal(@"E:\Juegos\Coop2D", new SettingsStore(_file).Load().InstallRoot);
    }

    [Fact]
    public void Load_Corrupt_RenamesFileAndReturnsDefaults()
    {
        File.WriteAllText(_file, "{ \"InstallRoot\": \"D:\\\\Juegos\" ,,, esto no es JSON");

        var settings = new SettingsStore(_file).Load();

        Assert.Null(settings.InstallRoot);
        Assert.False(File.Exists(_file));
        var copy = Assert.Single(CorruptCopies());
        Assert.Matches(@"settings\.corrupt-\d{14}\.json$", copy);
    }

    [Fact]
    public void Load_CorruptWithBackup_UsesBackupAndQuarantines()
    {
        File.WriteAllText(_file, "[1, 2, 3]"); // JSON válido pero no es un objeto
        File.WriteAllText(_file + ".bak", """{ "MaxDownloadSpeedKBps": 512 }""");

        var settings = new SettingsStore(_file).Load();

        Assert.Equal(512, settings.MaxDownloadSpeedKBps);
        Assert.Single(CorruptCopies());
    }

    [Fact]
    public void Load_ManyCorruptFiles_KeepsOnlyThreeNewest()
    {
        foreach (var stamp in new[] { "20200101000000", "20210101000000", "20220101000000", "20230101000000" })
            File.WriteAllText(_temp.Combine($"settings.corrupt-{stamp}.json"), "x");
        File.WriteAllText(_file, "no es json");

        new SettingsStore(_file).Load();

        var copies = CorruptCopies().Select(Path.GetFileName).ToList();
        Assert.Equal(3, copies.Count);
        Assert.DoesNotContain("settings.corrupt-20200101000000.json", copies);
        Assert.DoesNotContain("settings.corrupt-20210101000000.json", copies);
        Assert.Contains("settings.corrupt-20230101000000.json", copies);
    }

    [Fact]
    public void Load_SanitizesInvalidValues()
    {
        File.WriteAllText(_file, """
            {
              // los comentarios y las comas finales se aceptan
              "MaxDownloadSpeedKBps": -5,
              "LaunchArguments": null,
              "AfterLaunch": "Explotar",
              "InstallRoot": "Juegos\\Relativa",
              "IncludePrereleases": "sí",
              "CheckLauncherUpdates": false,
              "PropiedadDelFuturo": { "a": 1 },
            }
            """);

        var settings = new SettingsStore(_file).Load();

        Assert.Equal(0, settings.MaxDownloadSpeedKBps);
        Assert.Equal("", settings.LaunchArguments);
        Assert.Equal(AfterLaunchBehavior.Minimize, settings.AfterLaunch);
        Assert.Null(settings.InstallRoot);
        Assert.False(settings.IncludePrereleases);
        Assert.False(settings.CheckLauncherUpdates); // un valor malo no tira el resto
        Assert.Empty(CorruptCopies());
    }

    [Theory]
    [InlineData("99", AfterLaunchBehavior.Minimize)]
    [InlineData("2", AfterLaunchBehavior.Close)]
    [InlineData("\"keepopen\"", AfterLaunchBehavior.KeepOpen)]
    [InlineData("\"Minimize, Close\"", AfterLaunchBehavior.Minimize)]
    public void Load_EnumValues_AreValidated(string rawValue, AfterLaunchBehavior expected)
    {
        File.WriteAllText(_file, $$"""{ "afterlaunch": {{rawValue}} }""");

        Assert.Equal(expected, new SettingsStore(_file).Load().AfterLaunch);
    }

    [Fact]
    public void Save_UnwritableLocation_ReturnsFalse()
    {
        var blocker = _temp.Combine("soy-un-archivo");
        File.WriteAllText(blocker, "x");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json"));

        Assert.False(store.Save(new LauncherSettings()));
        Assert.NotNull(store.Load()); // y Load nunca lanza
    }

    [Fact]
    public void Save_ConcurrentCalls_DoNotThrowAndLeaveValidFile()
    {
        var store = new SettingsStore(_file);

        Parallel.For(0, 40, i => Assert.True(store.Save(new LauncherSettings { MaxDownloadSpeedKBps = i })));

        var loaded = new SettingsStore(_file).Load();
        Assert.InRange(loaded.MaxDownloadSpeedKBps, 0, 39);
        Assert.Empty(CorruptCopies());
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    [Fact]
    public void FilePath_IsTheGivenPath()
    {
        Assert.Equal(_file, new SettingsStore(_file).FilePath);
        Assert.ThrowsAny<ArgumentException>(() => new SettingsStore(" "));
    }

    // ---------------------------------------------------------------- campos de 1.0.5 (anuncios, sesiones, perfiles, dev)

    [Fact]
    public void Load_NoFile_NewFieldsHaveDefaults()
    {
        var settings = new SettingsStore(_file).Load();

        Assert.Empty(settings.DismissedAnnouncementIds);
        Assert.Empty(settings.RemindedPlaytestIds);
        Assert.True(settings.PlaytestRemindersEnabled);
        Assert.Equal(LaunchProfile.Default, settings.LaunchProfile);
        Assert.False(settings.DeveloperMode);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNewFields()
    {
        var original = new LauncherSettings
        {
            DismissedAnnouncementIds = new List<string> { "bienvenida", "pre2-listo" },
            RemindedPlaytestIds = new List<string> { "pt-2026-10-24" },
            PlaytestRemindersEnabled = false,
            LaunchProfile = LaunchProfile.Windowed1080,
            DeveloperMode = true,
        };

        Assert.True(new SettingsStore(_file).Save(original));
        var loaded = new SettingsStore(_file).Load();

        Assert.Equal(new[] { "bienvenida", "pre2-listo" }, loaded.DismissedAnnouncementIds);
        Assert.Equal(new[] { "pt-2026-10-24" }, loaded.RemindedPlaytestIds);
        Assert.False(loaded.PlaytestRemindersEnabled);
        Assert.Equal(LaunchProfile.Windowed1080, loaded.LaunchProfile);
        Assert.True(loaded.DeveloperMode);
        Assert.Contains("\"Windowed1080\"", File.ReadAllText(_file), StringComparison.Ordinal); // enum como texto
    }

    [Fact]
    public void Load_OldFileWithoutNewFields_KeepsOldValuesAndDefaults()
    {
        // Un settings.json de 1.0.4 (sin los campos nuevos) se sigue leyendo igual.
        File.WriteAllText(_file, """{ "SchemaVersion": 1, "AutoDownloadUpdates": true, "LaunchArguments": "-monitor 2" }""");

        var settings = new SettingsStore(_file).Load();

        Assert.True(settings.AutoDownloadUpdates);
        Assert.Equal("-monitor 2", settings.LaunchArguments);
        Assert.Empty(settings.DismissedAnnouncementIds);
        Assert.True(settings.PlaytestRemindersEnabled);
        Assert.Equal(LaunchProfile.Default, settings.LaunchProfile);
    }

    [Fact]
    public void Load_SanitizesIdLists()
    {
        File.WriteAllText(_file, """
            {
              "DismissedAnnouncementIds": ["a", "", "  ", null, " b ", "a", 7, {"x": 1}, "c"],
              "RemindedPlaytestIds": null,
              "DeveloperMode": true
            }
            """);

        var settings = new SettingsStore(_file).Load();

        // Sin vacíos, sin no-textos, sin repetidos (se queda la última aparición) y con espacios recortados.
        Assert.Equal(new[] { "b", "a", "c" }, settings.DismissedAnnouncementIds);
        Assert.NotNull(settings.RemindedPlaytestIds);
        Assert.Empty(settings.RemindedPlaytestIds);
        Assert.True(settings.DeveloperMode); // lo demás se lee igual
        Assert.Empty(CorruptCopies());
    }

    [Fact]
    public void Load_IdListWithWrongType_UsesEmptyListAndKeepsTheRest()
    {
        File.WriteAllText(_file, """{ "RemindedPlaytestIds": "pt-1", "PlaytestRemindersEnabled": false }""");

        var settings = new SettingsStore(_file).Load();

        Assert.Empty(settings.RemindedPlaytestIds);
        Assert.False(settings.PlaytestRemindersEnabled);
    }

    [Fact]
    public void Load_TooManyIds_KeepsTheMostRecent200()
    {
        var ids = Enumerable.Range(1, 250).Select(i => $"\"id-{i}\"");
        File.WriteAllText(_file, $$"""{ "DismissedAnnouncementIds": [{{string.Join(",", ids)}}] }""");

        var loaded = new SettingsStore(_file).Load().DismissedAnnouncementIds;

        Assert.Equal(200, loaded.Count);
        Assert.Equal("id-51", loaded[0]);
        Assert.Equal("id-250", loaded[^1]);
    }

    [Fact]
    public void Save_SanitizesIdListsWithoutTouchingTheCallerInstance()
    {
        var dismissed = Enumerable.Range(1, 230).Select(i => $"id-{i}").ToList();
        dismissed.Add("id-5");      // repetido: pasa a ser de los más recientes
        dismissed.Add("   ");
        var settings = new LauncherSettings
        {
            DismissedAnnouncementIds = dismissed,
            RemindedPlaytestIds = null!,
        };

        Assert.True(new SettingsStore(_file).Save(settings));

        Assert.Equal(232, settings.DismissedAnnouncementIds.Count); // el objeto del llamador no cambia
        Assert.Null(settings.RemindedPlaytestIds);

        var loaded = new SettingsStore(_file).Load();
        Assert.Equal(200, loaded.DismissedAnnouncementIds.Count);
        Assert.Equal("id-32", loaded.DismissedAnnouncementIds[0]);
        Assert.Equal("id-5", loaded.DismissedAnnouncementIds[^1]);
        Assert.Single(loaded.DismissedAnnouncementIds, id => id == "id-5");
        Assert.Empty(loaded.RemindedPlaytestIds);
    }

    [Fact]
    public void SanitizeIdList_DropsOverlongIdsAndReturnsANewList()
    {
        var source = new List<string> { "ok", new string('x', SettingsStore.MaxRememberedIdLength + 1), "ok2" };

        var result = SettingsStore.SanitizeIdList(source);

        Assert.Equal(new[] { "ok", "ok2" }, result);
        Assert.NotSame(source, result);
        Assert.Empty(SettingsStore.SanitizeIdList(null));
    }

    [Theory]
    [InlineData("\"Borderless\"", LaunchProfile.Borderless)]
    [InlineData("\"fullscreen\"", LaunchProfile.Fullscreen)]
    [InlineData("1", LaunchProfile.Windowed720)]
    [InlineData("\"Ventana\"", LaunchProfile.Default)]
    [InlineData("42", LaunchProfile.Default)]
    [InlineData("null", LaunchProfile.Default)]
    [InlineData("true", LaunchProfile.Default)]
    public void Load_LaunchProfile_InvalidValuesFallBackToDefault(string rawValue, LaunchProfile expected)
    {
        File.WriteAllText(_file, $$"""{ "LaunchProfile": {{rawValue}}, "DeveloperMode": true }""");

        var settings = new SettingsStore(_file).Load();

        Assert.Equal(expected, settings.LaunchProfile);
        Assert.True(settings.DeveloperMode);
    }
}
