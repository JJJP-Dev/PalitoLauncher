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
}
