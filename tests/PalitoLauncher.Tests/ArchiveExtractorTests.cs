using System.IO.Compression;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.B;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;

namespace PalitoLauncher.Tests;

public sealed class ArchiveExtractorTests : IDisposable
{
    private static readonly DateTime EntryTime = new(2020, 5, 4, 3, 2, 10, DateTimeKind.Local);

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Crea un zip con las entradas dadas (data null = carpeta).</summary>
    private string CreateZip(string name, params (string Entry, byte[]? Data)[] entries)
    {
        var path = _temp.Combine(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, data) in entries)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(EntryTime);
            if (data is not null)
            {
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return path;
    }

    private string Create7z(string name, params (string Entry, byte[] Data)[] entries)
    {
        var path = _temp.Combine(name);
        using var file = File.Create(path);
        using var writer = new SevenZipWriter(file, new SevenZipWriterOptions(CompressionType.LZMA));
        foreach (var (entryName, data) in entries)
        {
            writer.Write(entryName, new MemoryStream(data), EntryTime);
        }
        return path;
    }

    /// <summary>Estructura real de una build: carpeta raíz con el nombre del asset y dentro la build de Unity.</summary>
    private string CreateRealisticZip(out Dictionary<string, byte[]> files)
    {
        files = new Dictionary<string, byte[]>
        {
            [@"2D_0.0.2_Protype2\Coop2D.exe"] = Bytes(70_000, 1),
            [@"2D_0.0.2_Protype2\UnityPlayer.dll"] = Bytes(150_000, 2),
            [@"2D_0.0.2_Protype2\Coop2D_Data\app.info"] = "PalitoGames\nCoop2D"u8.ToArray(),
            [@"2D_0.0.2_Protype2\Coop2D_Data\Managed\Assembly-CSharp.dll"] = Bytes(30_000, 3),
            [@"2D_0.0.2_Protype2\empty.txt"] = [],
        };
        var entries = new List<(string, byte[]?)> { ("2D_0.0.2_Protype2/", null), ("2D_0.0.2_Protype2/MonoBleedingEdge/", null) };
        entries.AddRange(files.Select(f => (f.Key.Replace('\\', '/'), (byte[]?)f.Value)));
        return CreateZip("2D_0.0.2_Protype2.zip", entries.ToArray());
    }

    [Fact]
    public void DetectFormat_RecognizesMagicBytesNotExtension()
    {
        var zip = CreateZip("build.rar", ("a.txt", "hola"u8.ToArray()));
        var emptyZip = _temp.Combine("empty.zip");
        using (ZipFile.Open(emptyZip, ZipArchiveMode.Create))
        {
        }
        var rar = _temp.WriteFile("fake.zip", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 1, 2, 3]);
        var sevenZip = Create7z("build.zip", ("a.txt", "hola"u8.ToArray()));
        var text = _temp.WriteFile("notes.zip", "esto no es un zip");
        var tiny = _temp.WriteFile("tiny.7z", [0x37, 0x7A]);

        Assert.Equal(ArchiveFormat.Zip, ArchiveExtractor.DetectFormat(zip));
        Assert.Equal(ArchiveFormat.Zip, ArchiveExtractor.DetectFormat(emptyZip));
        Assert.Equal(ArchiveFormat.Rar, ArchiveExtractor.DetectFormat(rar));
        Assert.Equal(ArchiveFormat.SevenZip, ArchiveExtractor.DetectFormat(sevenZip));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveExtractor.DetectFormat(text));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveExtractor.DetectFormat(tiny));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveExtractor.DetectFormat(_temp.Combine("no-existe.zip")));
    }

    [Fact]
    public void GetUncompressedSize_SumsFileSizes()
    {
        var zip = CreateRealisticZip(out var files);
        var sevenZip = Create7z("b.7z", ("x/a.bin", Bytes(1234)), ("x/b.bin", Bytes(4321)));

        Assert.Equal(files.Values.Sum(f => (long)f.Length), ArchiveExtractor.GetUncompressedSize(zip));
        Assert.Equal(1234 + 4321, ArchiveExtractor.GetUncompressedSize(sevenZip));
    }

    [Fact]
    public void GetUncompressedSize_UnknownOrCorrupt_ThrowsLauncherException()
    {
        var text = _temp.WriteFile("x.zip", "texto");
        var badZip = _temp.WriteFile("bad.zip", [0x50, 0x4B, 0x03, 0x04, .. Bytes(500)]);

        Assert.Equal(ArchiveExtractor.CorruptMessage, Assert.Throws<LauncherException>(() => ArchiveExtractor.GetUncompressedSize(text)).Message);
        Assert.Equal(ArchiveExtractor.CorruptMessage, Assert.Throws<LauncherException>(() => ArchiveExtractor.GetUncompressedSize(badZip)).Message);
        Assert.Throws<LauncherException>(() => ArchiveExtractor.GetUncompressedSize(_temp.Combine("no-existe.zip")));
    }

    [Fact]
    public void DetectFormatOrThrow_PropagatesReadErrors_WhileDetectFormatDoesNot()
    {
        Assert.Throws<FileNotFoundException>(() => ArchiveExtractor.DetectFormatOrThrow(_temp.Combine("no-existe.zip")));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveExtractor.DetectFormatOrThrow(_temp.WriteFile("texto.zip", "hola")));

        var zip = CreateZip("a.zip", ("a.txt", "x"u8.ToArray()));
        using (FileBlocks.DenyRead(zip))
        {
            Assert.Throws<UnauthorizedAccessException>(() => ArchiveExtractor.DetectFormatOrThrow(zip));
            Assert.Equal(ArchiveFormat.Unknown, ArchiveExtractor.DetectFormat(zip));
        }
    }

    [Fact]
    public async Task ArchiveBlockedByAntivirus_IsReportedAsAntivirusNotCorrupt()
    {
        var root = _temp.Combine("Coop2D");
        var zip = CreateRealisticZip(out _);
        var dest = Path.Combine(root, ".launcher", "staging", "a1b2c3d4");

        using (FileBlocks.DenyRead(zip))
        {
            var sizeError = Assert.Throws<LauncherException>(() => ArchiveExtractor.GetUncompressedSize(zip));
            var extractError = await Assert.ThrowsAsync<LauncherException>(() => ArchiveExtractor.ExtractAsync(zip, dest, null, CancellationToken.None));

            Assert.StartsWith("El antivirus bloqueó un archivo del juego (2D_0.0.2_Protype2.zip)", sizeError.Message);
            Assert.Equal(
                $"El antivirus bloqueó un archivo del juego (2D_0.0.2_Protype2.zip). Añade una exclusión para {root} y vuelve a intentarlo.",
                extractError.Message);
        }
    }

    [Fact]
    public async Task ArchiveInUseByAnotherProgram_IsReportedAsInUseNotCorrupt()
    {
        var zip = CreateRealisticZip(out _);

        using (FileBlocks.LockExclusive(zip))
        {
            Assert.Equal(ArchiveExtractor.InUseMessage, Assert.Throws<LauncherException>(() => ArchiveExtractor.GetUncompressedSize(zip)).Message);
            var ex = await Assert.ThrowsAsync<LauncherException>(
                () => ArchiveExtractor.ExtractAsync(zip, _temp.Combine("dest"), null, CancellationToken.None));
            Assert.Equal(ArchiveExtractor.InUseMessage, ex.Message);
        }
    }

    [Fact]
    public async Task ExtractAsync_Zip_ExtractsEverythingWithDatesAndProgress()
    {
        var zip = CreateRealisticZip(out var files);
        var dest = _temp.Combine("staging", "a1b2c3d4");
        var progress = new SyncProgress<ExtractProgress>();

        var extracted = await ArchiveExtractor.ExtractAsync(zip, dest, progress, CancellationToken.None);

        Assert.Equal(files.Count, extracted.Count);
        foreach (var (relative, data) in files)
        {
            var full = Path.Combine(dest, relative);
            Assert.True(File.Exists(full), relative);
            Assert.Equal(data, await File.ReadAllBytesAsync(full));
            Assert.Contains(new ExtractedFile(relative, data.Length), extracted);
            Assert.Equal(EntryTime, File.GetLastWriteTime(full));
        }
        Assert.True(Directory.Exists(Path.Combine(dest, "2D_0.0.2_Protype2", "MonoBleedingEdge")));
        Assert.All(extracted, f => Assert.DoesNotContain('/', f.RelativePath));

        var last = progress.Values.Last();
        Assert.Equal(files.Count, last.EntriesTotal);
        Assert.Equal(files.Count, last.EntriesDone);
        Assert.Equal(files.Values.Sum(f => (long)f.Length), last.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(1.0, last.Fraction);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("game/../../evil.txt")]
    [InlineData("/abs.txt")]
    [InlineData("\\abs.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("C:evil.txt")]
    [InlineData("game/file.txt:secreto")]
    [InlineData("game/CON")]
    [InlineData("game/nul.txt")]
    public async Task ExtractAsync_MaliciousPath_IsRejectedAndNothingIsWritten(string maliciousEntry)
    {
        var zip = CreateZip("evil.zip", ("game/ok.txt", "bien"u8.ToArray()), (maliciousEntry, "mal"u8.ToArray()));
        var dest = _temp.Combine("out", "dest");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => ArchiveExtractor.ExtractAsync(zip, dest, null, CancellationToken.None));

        Assert.Equal(ArchiveExtractor.InvalidPathsMessage, ex.Message);
        Assert.False(File.Exists(_temp.Combine("out", "evil.txt")));
        Assert.False(File.Exists(_temp.Combine("evil.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
    }

    [Fact]
    public async Task ExtractAsync_DotDotThatStaysInside_IsAllowed()
    {
        var zip = CreateZip("ok.zip", ("game/sub/../file.txt", "dentro"u8.ToArray()));
        var dest = _temp.Combine("dest");

        var extracted = await ArchiveExtractor.ExtractAsync(zip, dest, null, CancellationToken.None);

        Assert.Equal(@"game\file.txt", Assert.Single(extracted).RelativePath);
        Assert.Equal("dentro", await File.ReadAllTextAsync(Path.Combine(dest, "game", "file.txt")));
    }

    [Fact]
    public async Task ExtractAsync_DuplicateEntries_LastOneWins()
    {
        var zip = CreateZip("dup.zip", ("a.txt", "primero"u8.ToArray()), ("a.txt", "segundo!!"u8.ToArray()));
        var dest = _temp.Combine("dest");

        var extracted = await ArchiveExtractor.ExtractAsync(zip, dest, null, CancellationToken.None);

        var file = Assert.Single(extracted);
        Assert.Equal(new ExtractedFile("a.txt", 9), file);
        Assert.Equal("segundo!!", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt")));
    }

    [Fact]
    public async Task ExtractAsync_DestinationNotEmpty_Throws()
    {
        var zip = CreateZip("a.zip", ("a.txt", "x"u8.ToArray()));
        var dest = _temp.Combine("dest");
        _temp.WriteFile(Path.Combine("dest", "existing.txt"), "ya estaba");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveExtractor.ExtractAsync(zip, dest, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_TruncatedZip_ThrowsCorruptMessage()
    {
        var zip = CreateRealisticZip(out _);
        var bytes = await File.ReadAllBytesAsync(zip);
        var truncated = _temp.WriteFile("truncated.zip", bytes.AsSpan(0, bytes.Length / 2).ToArray());

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => ArchiveExtractor.ExtractAsync(truncated, _temp.Combine("dest"), null, CancellationToken.None));

        Assert.Equal(ArchiveExtractor.CorruptMessage, ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_UnknownFormat_ThrowsCorruptMessage()
    {
        var text = _temp.WriteFile("build.zip", "<html>no es un zip</html>");

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => ArchiveExtractor.ExtractAsync(text, _temp.Combine("dest"), null, CancellationToken.None));

        Assert.Equal(ArchiveExtractor.CorruptMessage, ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_FakeRar_ThrowsCorruptMessage()
    {
        var rar = _temp.WriteFile("build.rar", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, .. Bytes(300)]);

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => ArchiveExtractor.ExtractAsync(rar, _temp.Combine("dest"), null, CancellationToken.None));

        Assert.Equal(ArchiveExtractor.CorruptMessage, ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_SevenZip_ExtractsFiles()
    {
        var exe = Bytes(50_000, 4);
        var data = Bytes(10_000, 5);
        var archive = Create7z("build.7z",
            ("2D_0.1.0/Coop2D.exe", exe),
            ("2D_0.1.0/Coop2D_Data/data.bin", data),
            ("2D_0.1.0/empty.txt", []));
        var dest = _temp.Combine("dest");
        var progress = new SyncProgress<ExtractProgress>();

        var extracted = await ArchiveExtractor.ExtractAsync(archive, dest, progress, CancellationToken.None);

        Assert.Equal(3, extracted.Count);
        Assert.Equal(exe, await File.ReadAllBytesAsync(Path.Combine(dest, "2D_0.1.0", "Coop2D.exe")));
        Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(dest, "2D_0.1.0", "Coop2D_Data", "data.bin")));
        Assert.Equal(0, new FileInfo(Path.Combine(dest, "2D_0.1.0", "empty.txt")).Length);
        Assert.Contains(new ExtractedFile(@"2D_0.1.0\Coop2D_Data\data.bin", data.Length), extracted);
        Assert.Equal(EntryTime, File.GetLastWriteTime(Path.Combine(dest, "2D_0.1.0", "Coop2D.exe")));
        var last = progress.Values.Last();
        Assert.Equal(3, last.EntriesDone);
        Assert.Equal(exe.Length + data.Length, last.BytesDone);
    }

    [Fact]
    public async Task ExtractAsync_SevenZipWithEscapingPath_IsRejected()
    {
        var archive = Create7z("evil.7z", ("ok.txt", "bien"u8.ToArray()), ("../evil.txt", "mal"u8.ToArray()));
        var dest = _temp.Combine("out", "dest");

        var ex = await Assert.ThrowsAsync<LauncherException>(() => ArchiveExtractor.ExtractAsync(archive, dest, null, CancellationToken.None));

        Assert.Equal(ArchiveExtractor.InvalidPathsMessage, ex.Message);
        Assert.False(File.Exists(_temp.Combine("out", "evil.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dest));
    }

    [Fact]
    public async Task ExtractAsync_CorruptSevenZip_ThrowsCorruptMessage()
    {
        var archive = Create7z("ok.7z", ("a.bin", Bytes(20_000)));
        var bytes = await File.ReadAllBytesAsync(archive);
        for (var i = 40; i < bytes.Length; i++)
        {
            bytes[i] ^= 0x5A;
        }
        var corrupt = _temp.WriteFile("corrupt.7z", bytes);

        var ex = await Assert.ThrowsAsync<LauncherException>(
            () => ArchiveExtractor.ExtractAsync(corrupt, _temp.Combine("dest"), null, CancellationToken.None));

        Assert.Equal(ArchiveExtractor.CorruptMessage, ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_Cancelled_ThrowsOperationCanceled()
    {
        var entries = Enumerable.Range(0, 30).Select(i => ($"game/file{i}.bin", (byte[]?)Bytes(50_000, i))).ToArray();
        var zip = CreateZip("many.zip", entries);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<ExtractProgress>(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ArchiveExtractor.ExtractAsync(zip, _temp.Combine("dest"), progress, cts.Token));
    }

    [Fact]
    public async Task ExtractAsync_AlreadyCancelled_ThrowsOperationCanceled()
    {
        var zip = CreateZip("a.zip", ("a.txt", "x"u8.ToArray()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ArchiveExtractor.ExtractAsync(zip, _temp.Combine("dest"), null, new CancellationToken(canceled: true)));
    }

    // ───────────────────────── FindContentRoot ─────────────────────────

    [Fact]
    public void FindContentRoot_ExeAtRoot_ReturnsRoot()
    {
        _temp.WriteFile("Coop2D.exe", "MZ");
        _temp.WriteFile(Path.Combine("Coop2D_Data", "app.info"), "x");

        Assert.Equal(_temp.Path, ArchiveExtractor.FindContentRoot(_temp.Path, "Coop2D.exe"));
    }

    [Fact]
    public void FindContentRoot_SingleFolderWithExe_RealCase()
    {
        _temp.WriteFile(Path.Combine("2D_0.0.2_Protype2", "Coop2D.exe"), "MZ");
        _temp.WriteFile(Path.Combine("2D_0.0.2_Protype2", "UnityPlayer.dll"), "x");

        Assert.Equal(_temp.Combine("2D_0.0.2_Protype2"), ArchiveExtractor.FindContentRoot(_temp.Path, "Coop2D.exe"));
    }

    [Fact]
    public void FindContentRoot_TwoLevels()
    {
        _temp.WriteFile(Path.Combine("a", "b", "Coop2D.exe"), "MZ");
        _temp.WriteFile(Path.Combine("a", "readme.txt"), "x");

        Assert.Equal(_temp.Combine("a", "b"), ArchiveExtractor.FindContentRoot(_temp.Path, "coop2d.EXE"));
    }

    [Fact]
    public void FindContentRoot_IgnoresMacOsFolderAlongside()
    {
        _temp.WriteFile(Path.Combine("__MACOSX", "Coop2D.exe"), "basura de mac");
        _temp.WriteFile(Path.Combine("Game", "Build", "Coop2D.exe"), "MZ");

        Assert.Equal(_temp.Combine("Game", "Build"), ArchiveExtractor.FindContentRoot(_temp.Path, "Coop2D.exe"));
    }

    [Fact]
    public void FindContentRoot_NoExeButSingleFolder_DescendsIntoIt()
    {
        _temp.WriteFile(Path.Combine("wrapper", "inner", "OtroNombre.exe"), "MZ");
        _temp.WriteFile(Path.Combine("wrapper", "inner", "UnityPlayer.dll"), "x");
        Directory.CreateDirectory(_temp.Combine("__MACOSX"));

        Assert.Equal(_temp.Combine("wrapper", "inner"), ArchiveExtractor.FindContentRoot(_temp.Path, "Coop2D.exe"));
    }

    [Fact]
    public void FindContentRoot_NoExeAndSeveralItems_ReturnsRoot()
    {
        _temp.WriteFile("Juego.exe", "MZ");
        _temp.WriteFile(Path.Combine("Data", "x.bin"), "x");
        _temp.WriteFile(Path.Combine("Otra", "y.bin"), "y");

        Assert.Equal(_temp.Path, ArchiveExtractor.FindContentRoot(_temp.Path, "Coop2D.exe"));
    }

    [Fact]
    public void FindContentRoot_EmptyOrMissing_ReturnsNull()
    {
        var empty = _temp.Combine("empty");
        Directory.CreateDirectory(empty);
        var onlyEmptyFolders = _temp.Combine("hollow");
        Directory.CreateDirectory(Path.Combine(onlyEmptyFolders, "a", "b"));

        Assert.Null(ArchiveExtractor.FindContentRoot(empty, "Coop2D.exe"));
        Assert.Null(ArchiveExtractor.FindContentRoot(onlyEmptyFolders, "Coop2D.exe"));
        Assert.Null(ArchiveExtractor.FindContentRoot(_temp.Combine("no-existe"), "Coop2D.exe"));
    }

    [Fact]
    public async Task ExtractThenFindContentRoot_RealisticZip()
    {
        var zip = CreateRealisticZip(out _);
        var dest = _temp.Combine("staging", "0badf00d");
        await ArchiveExtractor.ExtractAsync(zip, dest, null, CancellationToken.None);

        Assert.Equal(Path.Combine(dest, "2D_0.0.2_Protype2"), ArchiveExtractor.FindContentRoot(dest, "Coop2D.exe"));
    }
}
