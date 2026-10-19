using System.IO.Compression;
using PalitoLauncher.Core.Diagnostics;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.F;

namespace PalitoLauncher.Tests;

public class DiagnosticReportTests
{
    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"Falta la entrada {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void GetEnvironmentSummary_ContainsBasicKeys()
    {
        var summary = DiagnosticReport.GetEnvironmentSummary();

        foreach (var key in new[] { "Launcher", "Windows", ".NET", "Ejecutable", "Cultura" })
        {
            Assert.True(summary.ContainsKey(key), $"Falta {key}");
            Assert.False(string.IsNullOrWhiteSpace(summary[key]));
        }
    }

    [Fact]
    public void Create_IncludesExpectedFilesAndNotesProblems()
    {
        using var tmp = new TempDir();
        var logsDir = tmp.Combine("logs");
        var recentLog = tmp.WriteFile("logs\\launcher-20261019.log", "linea de log reciente");
        var oldLog = tmp.WriteFile("logs\\launcher-20260101.log", "log viejo");
        File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow.AddDays(-10));
        var settings = tmp.WriteFile("settings.json", "{ \"InstallRoot\": null }");

        var installRoot = tmp.Combine("Juegos", "Coop2D");
        tmp.WriteFile("Juegos\\Coop2D\\game\\palito_install.json", "{ \"Tag\": \"0.0.2-Prototype-2\" }");

        var unityDir = tmp.Combine("LocalLow", "PalitoGames", "Coop2D");
        var playerLog = tmp.WriteFile("LocalLow\\PalitoGames\\Coop2D\\Player.log", "inicio de Player.log\n");

        var tempDir = tmp.Combine("Temp");
        var olderCrash = tmp.WriteFile("Temp\\PalitoGames\\Coop2D\\Crashes\\Crash_1\\crash.dmp", "viejo");
        tmp.WriteFile("Temp\\PalitoGames\\Coop2D\\Crashes\\Crash_2\\crash.dmp", "nuevo");
        tmp.WriteFile("Temp\\PalitoGames\\Coop2D\\Crashes\\Crash_2\\sub\\error.log", "detalle");
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(olderCrash)!, DateTime.UtcNow.AddDays(-2));
        Directory.SetLastWriteTimeUtc(tmp.Combine("Temp", "PalitoGames", "Coop2D", "Crashes", "Crash_2"), DateTime.UtcNow);

        var context = new DiagnosticContext
        {
            Summary = new Dictionary<string, string?> { ["Estado"] = "Ready", ["Versión instalada"] = "0.0.2 · Prototipo 2", ["Nulo"] = null },
            InstallRoot = installRoot,
            UnityDataDir = unityDir,
        };

        var outDir = tmp.Combine("Escritorio");
        string zipPath;
        // Player.log abierto por "Unity" mientras se crea el informe (se puede leer igualmente).
        using (var writer = new FileStream(playerLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write("escribiendo\n"u8);
            writer.Flush();
            zipPath = DiagnosticReport.Create(context, outDir, logsDir, settings, tempDir, CancellationToken.None);
        }

        Assert.True(File.Exists(zipPath));
        Assert.StartsWith("Coop2D-informe-", Path.GetFileName(zipPath));
        Assert.EndsWith(".zip", zipPath);
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("summary.txt", names);
        Assert.Contains("logs/launcher-20261019.log", names);
        Assert.DoesNotContain("logs/launcher-20260101.log", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("game/palito_install.json", names);
        Assert.Contains("unity/Player.log", names);
        Assert.DoesNotContain("unity/Player-prev.log", names);
        Assert.Contains("crashes/Crash_2/crash.dmp", names);
        Assert.Contains("crashes/Crash_2/sub/error.log", names);
        Assert.DoesNotContain("crashes/Crash_1/crash.dmp", names);

        Assert.Equal("linea de log reciente", ReadEntry(zip, "logs/launcher-20261019.log"));
        Assert.Contains("escribiendo", ReadEntry(zip, "unity/Player.log"));

        var summary = ReadEntry(zip, "summary.txt");
        Assert.Contains("Estado: Ready", summary);
        Assert.Contains("Versión instalada: 0.0.2 · Prototipo 2", summary);
        Assert.Contains("Launcher:", summary);
        Assert.Contains(installRoot, summary);
        Assert.Contains("Espacio libre", summary);
        Assert.Contains("unity/Player-prev.log", summary); // anotado como no encontrado
        Assert.True(File.Exists(recentLog));
    }

    [Fact]
    public void Create_LockedFile_IsNotedAndReportStillCreated()
    {
        using var tmp = new TempDir();
        var logsDir = tmp.Combine("logs");
        var locked = tmp.WriteFile("logs\\launcher-bloqueado.log", "no se puede leer");
        tmp.WriteFile("logs\\launcher-ok.log", "ok");

        string zipPath;
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            zipPath = DiagnosticReport.Create(new DiagnosticContext(), tmp.Combine("out"), logsDir, tmp.Combine("no-settings.json"),
                tmp.Combine("Temp"), CancellationToken.None);
        }

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("logs/launcher-ok.log", names);
        Assert.DoesNotContain("logs/launcher-bloqueado.log", names);
        var summary = ReadEntry(zip, "summary.txt");
        Assert.Contains("logs/launcher-bloqueado.log: no se pudo leer", summary);
        Assert.Contains("settings.json: no encontrado", summary);
    }

    [Fact]
    public void Create_HugeLog_IsTruncatedToLimit()
    {
        using var tmp = new TempDir();
        var unityDir = tmp.Combine("LocalLow", "PalitoGames", "Coop2D");
        Directory.CreateDirectory(unityDir);
        var big = Path.Combine(unityDir, "Player.log");
        using (var fs = new FileStream(big, FileMode.Create))
        {
            fs.SetLength(DiagnosticReport.MaxFileBytes + 1024 * 1024);
            fs.Seek(-4, SeekOrigin.End);
            fs.Write("FIN!"u8);
        }

        var zipPath = DiagnosticReport.Create(new DiagnosticContext { UnityDataDir = unityDir }, tmp.Combine("out"),
            tmp.Combine("logs"), tmp.Combine("settings.json"), tmp.Combine("Temp"), CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("unity/Player.log");
        Assert.NotNull(entry);
        Assert.Equal(DiagnosticReport.MaxFileBytes, entry.Length);
        Assert.Contains("recortado", ReadEntry(zip, "summary.txt"));
    }

    [Fact]
    public void Create_TwoReportsInSameMinute_GetDifferentNames()
    {
        using var tmp = new TempDir();
        var outDir = tmp.Combine("out");
        var first = DiagnosticReport.Create(new DiagnosticContext(), outDir, tmp.Combine("logs"), tmp.Combine("s.json"),
            tmp.Combine("Temp"), CancellationToken.None);
        var second = DiagnosticReport.Create(new DiagnosticContext(), outDir, tmp.Combine("logs"), tmp.Combine("s.json"),
            tmp.Combine("Temp"), CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void GetUniqueZipPath_AddsSuffixWhenTaken()
    {
        using var tmp = new TempDir();
        var now = new DateTime(2026, 10, 19, 13, 45, 0);
        var first = DiagnosticReport.GetUniqueZipPath(tmp.Path, now);
        Assert.Equal(Path.Combine(tmp.Path, "Coop2D-informe-20261019-1345.zip"), first);

        File.WriteAllText(first, "x");
        Assert.Equal(Path.Combine(tmp.Path, "Coop2D-informe-20261019-1345-2.zip"), DiagnosticReport.GetUniqueZipPath(tmp.Path, now));
    }

    [Fact]
    public void Create_InvalidOutputDir_ThrowsLauncherException()
    {
        using var tmp = new TempDir();
        var file = tmp.WriteFile("soy-un-archivo", "x");

        Assert.Throws<LauncherException>(() => DiagnosticReport.Create(new DiagnosticContext(), Path.Combine(file, "sub"),
            tmp.Combine("logs"), tmp.Combine("s.json"), tmp.Combine("Temp"), CancellationToken.None));
        Assert.Throws<LauncherException>(() => DiagnosticReport.Create(new DiagnosticContext(), "  ",
            tmp.Combine("logs"), tmp.Combine("s.json"), tmp.Combine("Temp"), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_Cancelled_ThrowsAndLeavesNothing()
    {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiagnosticReport.CreateAsync(new DiagnosticContext(), tmp.Combine("out"), cts.Token));
        Assert.False(Directory.Exists(tmp.Combine("out")) && Directory.EnumerateFiles(tmp.Combine("out")).Any());
    }

    [Fact]
    public async Task CreateAsync_MinimalContext_CreatesZipWithSummary()
    {
        using var tmp = new TempDir();

        var zipPath = await DiagnosticReport.CreateAsync(new DiagnosticContext(), tmp.Path, CancellationToken.None);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.NotNull(zip.GetEntry("summary.txt"));
    }
}
