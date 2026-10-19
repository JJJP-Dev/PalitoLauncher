using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Versioning;
using PalitoLauncher.Tests.TestHelpers.F;

namespace PalitoLauncher.Tests;

public class LauncherInstallationTests
{
    private const string Installed = @"C:\Users\amigo\AppData\Local\Programs\PalitoLauncher\PalitoLauncher.exe";
    private const string Downloaded = @"C:\Users\amigo\Downloads\PalitoLauncher.exe";

    private static readonly GameVersion V100 = new(1, 0, 0);

    private static LauncherInstallDecision Decide(string? processPath, GameVersion? installedVersion, bool installedExists,
        params string[] args) =>
        LauncherInstallation.Decide(processPath, Installed, V100, installedVersion, installedExists, args);

    [Theory]
    [InlineData("--portable")]
    [InlineData("--PORTABLE")]
    [InlineData("--post-update")]
    public void Decide_PortableOrPostUpdate_Continues(string arg)
    {
        Assert.Equal(LauncherInstallDecision.Continue, Decide(Downloaded, null, false, arg, "1234"));
        Assert.Equal(LauncherInstallDecision.Continue, Decide(Downloaded, new GameVersion(0, 9, 0), true, "x", arg));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE")]
    public void Decide_NoProcessPathOrDotnet_Continues(string? processPath)
    {
        Assert.Equal(LauncherInstallDecision.Continue, Decide(processPath, null, false));
    }

    [Theory]
    [InlineData(Installed)]
    [InlineData(@"c:\users\AMIGO\appdata\local\programs\palitolauncher\palitolauncher.EXE")]
    [InlineData(@"C:\Users\amigo\AppData\Local\Programs\PalitoLauncher\.\PalitoLauncher.exe")]
    public void Decide_AlreadyInstalledCopy_ContinuesAsInstalled(string processPath)
    {
        Assert.Equal(LauncherInstallDecision.ContinueAsInstalled, Decide(processPath, V100, true));
    }

    [Fact]
    public void Decide_InstalledSameOrNewer_LaunchesInstalled()
    {
        Assert.Equal(LauncherInstallDecision.LaunchInstalled, Decide(Downloaded, new GameVersion(1, 0, 0), true));
        Assert.Equal(LauncherInstallDecision.LaunchInstalled, Decide(Downloaded, new GameVersion(1, 1, 0), true));
        Assert.Equal(LauncherInstallDecision.LaunchInstalled, Decide(Downloaded, new GameVersion(2, 0, 0), true, "--algo"));
    }

    [Fact]
    public void Decide_InstalledOlderMissingOrUnreadable_CopiesAndLaunches()
    {
        Assert.Equal(LauncherInstallDecision.CopyAndLaunch, Decide(Downloaded, new GameVersion(0, 9, 9), true));
        Assert.Equal(LauncherInstallDecision.CopyAndLaunch, Decide(Downloaded, new GameVersion(1, 0, 0, 0, "beta", 1), true));
        Assert.Equal(LauncherInstallDecision.CopyAndLaunch, Decide(Downloaded, null, false));
        Assert.Equal(LauncherInstallDecision.CopyAndLaunch, Decide(Downloaded, null, true));
        // Un valor de versión con la copia ya borrada no cuenta.
        Assert.Equal(LauncherInstallDecision.CopyAndLaunch, Decide(Downloaded, new GameVersion(5, 0, 0), false));
    }

    [Fact]
    public void EnsureInstalled_InTestHost_ContinuesWithoutRelaunching()
    {
        // El host de tests no es el exe publicado: nunca se copia ni se relanza.
        Assert.Equal(LauncherInstallOutcome.Continue, LauncherInstallation.EnsureInstalled(Array.Empty<string>()));
        Assert.Equal(LauncherInstallOutcome.Continue, LauncherInstallation.EnsureInstalled(new[] { "--portable" }));
    }

    [Fact]
    public void GetShortcutPath_UsesConfiguredName()
    {
        Assert.Equal(@"C:\Menu\Coop2D.lnk", LauncherInstallation.GetShortcutPath(@"C:\Menu"));
    }

    [Fact]
    public void ReadFileVersion_ReadsSystemExeAndRejectsOthers()
    {
        using var tmp = new TempDir();
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        Assert.NotNull(LauncherInstallation.ReadFileVersion(notepad));
        Assert.Null(LauncherInstallation.ReadFileVersion(tmp.WriteFile("no-es-exe.exe", "hola")));
        Assert.Null(LauncherInstallation.ReadFileVersion(tmp.Combine("no-existe.exe")));
    }

    [Fact]
    public void TryCopyTo_CopiesIntoNewFolder()
    {
        using var tmp = new TempDir();
        var source = tmp.WriteFile("Descargas\\PalitoLauncher.exe", "contenido nuevo");
        var dest = tmp.Combine("Programs", "PalitoLauncher", "PalitoLauncher.exe");

        Assert.True(LauncherInstallation.TryCopyTo(source, dest, moveAttempts: 1, moveDelayMs: 10));

        Assert.Equal("contenido nuevo", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + ".tmp"));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void TryCopyTo_OverwritesOlderCopy()
    {
        using var tmp = new TempDir();
        var source = tmp.WriteFile("nuevo.exe", "v2");
        var dest = tmp.WriteFile("Programs\\PalitoLauncher.exe", "v1");

        Assert.True(LauncherInstallation.TryCopyTo(source, dest, moveAttempts: 1, moveDelayMs: 10));
        Assert.Equal("v2", File.ReadAllText(dest));
    }

    [Fact]
    public void TryCopyTo_LockedDestination_ReturnsFalseAndCleansTemp()
    {
        using var tmp = new TempDir();
        var source = tmp.WriteFile("nuevo.exe", "v2");
        var dest = tmp.WriteFile("Programs\\PalitoLauncher.exe", "v1");

        using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(LauncherInstallation.TryCopyTo(source, dest, moveAttempts: 2, moveDelayMs: 10));
        }

        Assert.Equal("v1", File.ReadAllText(dest));
        Assert.False(File.Exists(dest + ".tmp"));
    }

    [Fact]
    public void TryCopyTo_MissingSource_ReturnsFalse()
    {
        using var tmp = new TempDir();
        Assert.False(LauncherInstallation.TryCopyTo(tmp.Combine("no-existe.exe"), tmp.Combine("dest", "x.exe"), 1, 10));
    }
}
