using System.Text;
using System.Text.RegularExpressions;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Tests.TestHelpers.C;

namespace PalitoLauncher.Tests;

/// <summary>Escritor de archivo (instancias independientes: no tocan el Log global).</summary>
public sealed class LogTests : IDisposable
{
    private static readonly DateTime Day1 = new(2026, 10, 19, 13, 45, 1, 123);
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    internal static string[] ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void Write_ProducesFormattedLineInDailyFile()
    {
        using var sink = new LogSink(_temp.Path, () => Day1, 1234);

        sink.Write(LogLevel.Info, "hola mundo", null);
        sink.Write(LogLevel.Warn, "cuidado", null);
        sink.Write(LogLevel.Debug, "detalle", null);

        var file = Path.Combine(_temp.Path, "launcher-20261019.log");
        Assert.Equal(file, sink.CurrentFile);
        var lines = ReadLinesShared(file);
        Assert.Equal(3, lines.Length);
        Assert.Matches(@"^2026-10-19 13:45:01\.123 \[INFO \] \[T\d+\] hola mundo$", lines[0]);
        Assert.Matches(@"^2026-10-19 13:45:01\.123 \[WARN \] \[T\d+\] cuidado$", lines[1]);
        Assert.Matches(@"^2026-10-19 13:45:01\.123 \[DEBUG\] \[T\d+\] detalle$", lines[2]);
    }

    [Fact]
    public void Write_WithException_AppendsFullExceptionOnFollowingLines()
    {
        using var sink = new LogSink(_temp.Path, () => Day1, 1);
        Exception captured;
        try { throw new InvalidOperationException("fallo interno", new IOException("disco")); }
        catch (Exception ex) { captured = ex; }

        sink.Write(LogLevel.Error, "algo salió mal", captured);

        var text = string.Join("\n", ReadLinesShared(sink.CurrentFile));
        Assert.StartsWith("2026-10-19 13:45:01.123 [ERROR] [T", text, StringComparison.Ordinal);
        Assert.Contains("algo salió mal\nSystem.InvalidOperationException: fallo interno", text, StringComparison.Ordinal);
        Assert.Contains("System.IO.IOException: disco", text, StringComparison.Ordinal);
        Assert.Contains("LogTests", text, StringComparison.Ordinal); // pila incluida
    }

    [Fact]
    public void Write_DayChange_RotatesToNewFile()
    {
        var now = Day1;
        using var sink = new LogSink(_temp.Path, () => now, 1);

        sink.Write(LogLevel.Info, "día 1", null);
        now = Day1.AddDays(1);
        sink.Write(LogLevel.Info, "día 2", null);

        var file1 = Path.Combine(_temp.Path, "launcher-20261019.log");
        var file2 = Path.Combine(_temp.Path, "launcher-20261020.log");
        Assert.Single(ReadLinesShared(file1), l => l.EndsWith("día 1", StringComparison.Ordinal));
        Assert.Single(ReadLinesShared(file2), l => l.EndsWith("día 2", StringComparison.Ordinal));
        Assert.Equal(file2, sink.CurrentFile);
    }

    [Fact]
    public void Write_UnwritableFolder_DoesNotThrow()
    {
        var blocker = _temp.Combine("soy-un-archivo");
        File.WriteAllText(blocker, "x");
        using var sink = new LogSink(Path.Combine(blocker, "logs"), () => Day1, 1);

        sink.Write(LogLevel.Info, "nadie me lee", null);
        sink.Write(LogLevel.Error, "tampoco", new Exception("x"));

        Assert.False(Directory.Exists(Path.Combine(blocker, "logs")));
    }

    [Fact]
    public void MainFileLockedExclusively_FallsBackToPidFile()
    {
        var main = Path.Combine(_temp.Path, "launcher-20261019.log");
        using var exclusive = new FileStream(main, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var sink = new LogSink(_temp.Path, () => Day1, 4321);

        sink.Write(LogLevel.Info, "al archivo alternativo", null);

        var fallback = Path.Combine(_temp.Path, "launcher-20261019-4321.log");
        Assert.Equal(fallback, sink.CurrentFile);
        Assert.Single(ReadLinesShared(fallback));
    }

    [Fact]
    public void TwoConcurrentWriters_NoLinesLostOrInterleaved()
    {
        // Simula dos procesos (p.ej. durante la auto-actualización) escribiendo el mismo archivo a la vez.
        using var a = new LogSink(_temp.Path, () => Day1, 1);
        using var b = new LogSink(_temp.Path, () => Day1, 2);
        const int perWriter = 1500;

        Parallel.Invoke(
            () => { for (var i = 0; i < perWriter; i++) a.Write(LogLevel.Info, $"A-{i:D5} " + new string('a', 40), null); },
            () => { for (var i = 0; i < perWriter; i++) b.Write(LogLevel.Info, $"B-{i:D5} " + new string('b', 40), null); });

        Assert.Equal(a.CurrentFile, b.CurrentFile);
        var lines = ReadLinesShared(a.CurrentFile);
        Assert.Equal(perWriter * 2, lines.Length);
        var pattern = new Regex(@"^2026-10-19 13:45:01\.123 \[INFO \] \[T\d+\] ([AB])-(\d{5}) (a{40}|b{40})$");
        Assert.All(lines, l => Assert.Matches(pattern, l));
        Assert.Equal(perWriter, lines.Count(l => l.Contains("] A-", StringComparison.Ordinal)));
        Assert.Equal(perWriter, lines.Count(l => l.Contains("] B-", StringComparison.Ordinal)));
    }

    [Fact]
    public void ConcurrentThreadsSameWriter_AllLinesWritten()
    {
        using var sink = new LogSink(_temp.Path, () => Day1, 1);

        Parallel.For(0, 800, i => sink.Write(LogLevel.Info, $"msg-{i}", null));

        Assert.Equal(800, ReadLinesShared(sink.CurrentFile).Length);
    }

    [Fact]
    public void OpenFile_CanBeReadAndDeletedWhileWriterIsAlive()
    {
        using var sink = new LogSink(_temp.Path, () => Day1, 1);
        sink.Write(LogLevel.Info, "uno", null);

        Assert.Single(ReadLinesShared(sink.CurrentFile)); // el usuario puede abrirlo
        File.Delete(sink.CurrentFile);                       // y borrarlo
        sink.Write(LogLevel.Info, "dos", null);              // sin que el logger falle
    }

    [Fact]
    public void CleanupOldLogs_DeletesOnlyOldLauncherLogs()
    {
        var now = DateTime.Now;
        var old = _temp.Combine("launcher-20200101.log");
        var recent = _temp.Combine("launcher-20261018.log");
        var other = _temp.Combine("otra-cosa.log");
        foreach (var f in new[] { old, recent, other })
            File.WriteAllText(f, "x");
        File.SetLastWriteTime(old, now.AddDays(-30));
        File.SetLastWriteTime(other, now.AddDays(-30));
        File.SetLastWriteTime(recent, now.AddDays(-1));

        LogSink.CleanupOldLogs(_temp.Path, keepDays: 7, now);
        LogSink.CleanupOldLogs(_temp.Combine("no-existe"), 7, now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }
}

[CollectionDefinition(nameof(GlobalLogCollection), DisableParallelization = true)]
public sealed class GlobalLogCollection
{
}

/// <summary>Fachada estática <see cref="Log"/> (estado global: se ejecuta sin paralelismo y se restablece al final).</summary>
[Collection(nameof(GlobalLogCollection))]
public sealed class LogFacadeTests : IDisposable
{
    private readonly TempDir _temp = new();

    public LogFacadeTests() => Log.ResetForTests();

    public void Dispose()
    {
        Log.ResetForTests();
        _temp.Dispose();
    }

    [Fact]
    public void BeforeInitialize_WritesNothingAndNeverThrows()
    {
        Log.Debug("d");
        Log.Info("i");
        Log.Warn("w", new Exception("x"));
        Log.Error("e", null);

        Assert.Null(Log.CurrentLogFile);
        Assert.Null(Log.LogsDirectory);
    }

    [Fact]
    public void Initialize_WritesToDailyFile_SecondCallIsIgnored_AndRotates()
    {
        var now = new DateTime(2026, 10, 19, 23, 59, 59, 900);
        Log.Clock = () => now;
        var dir = _temp.Combine("logs");

        Log.Initialize(dir);
        Log.Initialize(_temp.Combine("otra"));
        Log.Info("primera línea");

        Assert.Equal(Path.GetFullPath(dir), Log.LogsDirectory);
        Assert.Equal(Path.Combine(dir, "launcher-20261019.log"), Log.CurrentLogFile);
        Assert.False(Directory.Exists(_temp.Combine("otra")));
        Assert.Contains(LogTests.ReadLinesShared(Log.CurrentLogFile!), l => l.EndsWith("[INFO ] [T" + Environment.CurrentManagedThreadId + "] primera línea", StringComparison.Ordinal));

        now = now.AddSeconds(1);
        Log.Warn("segunda línea");
        Assert.Equal(Path.Combine(dir, "launcher-20261020.log"), Log.CurrentLogFile);
        Assert.Single(LogTests.ReadLinesShared(Log.CurrentLogFile!));
    }

    [Fact]
    public void Initialize_DeletesLogsOlderThanKeepDays()
    {
        var dir = _temp.Combine("logs");
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "launcher-20200101.log");
        File.WriteAllText(old, "viejo");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-10));

        Log.Initialize(dir, keepDays: 7);

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Initialize_UnwritableOrEmptyFolder_NeverThrows()
    {
        var blocker = _temp.Combine("archivo");
        File.WriteAllText(blocker, "x");

        Log.Initialize("");
        Log.Initialize(Path.Combine(blocker, "logs"));
        Log.Info("sin carpeta");
        Log.Error("sigue sin lanzar", new InvalidOperationException());

        Assert.NotNull(Log.LogsDirectory);
    }
}
