using System.Text;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.B;

namespace PalitoLauncher.Tests;

public sealed class FileSystemUtilTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    // ───────────────────────── Formatos ─────────────────────────

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(999, "999 B")]
    [InlineData(1500, "1.5 KB")]
    [InlineData(31_900_000, "31.9 MB")]
    [InlineData(70_782_621, "70.8 MB")]
    [InlineData(999_999, "1.0 MB")]
    [InlineData(1_230_000_000, "1.23 GB")]
    [InlineData(-1500, "-1.5 KB")]
    public void FormatBytes_UsesInvariantCultureAndDecimalUnits(long bytes, string expected)
    {
        Assert.Equal(expected, FileSystemUtil.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_IgnoresCurrentCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            Assert.Equal("70.8 MB", FileSystemUtil.FormatBytes(70_782_621));
            Assert.Equal("5.3 MB/s", FileSystemUtil.FormatSpeed(5_300_000));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, "—")]
    [InlineData(-10, "—")]
    [InlineData(5_300_000, "5.3 MB/s")]
    [InlineData(512, "512 B/s")]
    public void FormatSpeed_Formats(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, FileSystemUtil.FormatSpeed(bytesPerSecond));
    }

    [Fact]
    public void FormatSpeed_NaN_IsDash() => Assert.Equal("—", FileSystemUtil.FormatSpeed(double.NaN));

    [Theory]
    [InlineData(12, "12 s")]
    [InlineData(0, "0 s")]
    [InlineData(-5, "0 s")]
    [InlineData(59, "59 s")]
    [InlineData(60, "1 min 00 s")]
    [InlineData(185, "3 min 05 s")]
    [InlineData(3720, "1 h 02 min")]
    [InlineData(90_000, "25 h 00 min")]
    public void FormatDuration_Formats(int seconds, string expected)
    {
        Assert.Equal(expected, FileSystemUtil.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatDuration_HugeValue_DoesNotThrow()
    {
        Assert.False(string.IsNullOrEmpty(FileSystemUtil.FormatDuration(TimeSpan.MaxValue)));
    }

    // ───────────────────────── Rutas ─────────────────────────

    [Theory]
    [InlineData(@"C:\Juegos\Coop2D", @"C:\Juegos", true)]
    [InlineData(@"C:\Juegos", @"C:\Juegos", true)]
    [InlineData(@"c:\juegos\", @"C:\Juegos", true)]
    [InlineData(@"C:/Juegos/Coop2D/game", @"C:\juegos\", true)]
    [InlineData(@"C:\Juegos2", @"C:\Juegos", false)]
    [InlineData(@"C:\Juegos\a\..\b", @"C:\Juegos", true)]
    [InlineData(@"C:\Juegos\..\Otra", @"C:\Juegos", false)]
    [InlineData(@"C:\x\y", @"C:\", true)]
    [InlineData(@"D:\Juegos", @"C:\Juegos", false)]
    [InlineData(@"", @"C:\Juegos", false)]
    [InlineData(@"C:\Juegos", @"", false)]
    public void IsSubPathOf_ComparesNormalizedPaths(string path, string parent, bool expected)
    {
        Assert.Equal(expected, FileSystemUtil.IsSubPathOf(path, parent));
    }

    [Theory]
    [InlineData(@"C:\a", @"c:\b\c", true)]
    [InlineData(@"C:\a", @"D:\a", false)]
    [InlineData(@"\\server\share\a", @"\\SERVER\share\b", true)]
    public void IsSameVolume_ComparesRoots(string a, string b, bool expected)
    {
        Assert.Equal(expected, FileSystemUtil.IsSameVolume(a, b));
    }

    [Fact]
    public void IsSameVolume_SameRootButDifferentRealVolume_IsFalse()
    {
        // "C:\Juegos" es una unión / punto de montaje que lleva a otra unidad: mismo prefijo, distinto volumen real.
        var from = _temp.Combine("actual", "Coop2D");
        var to = _temp.Combine("Juegos", "Coop2D");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(_temp.Combine("Juegos"));
        uint? Serial(string dir) => FileSystemUtil.IsSubPathOf(dir, _temp.Combine("Juegos")) ? 2u : 1u;

        Assert.False(FileSystemUtil.IsSameVolume(from, to, Serial));
        Assert.True(FileSystemUtil.IsSameVolume(from, _temp.Combine("actual", "Otro"), Serial));
        // Si no se puede consultar el volumen, decide el prefijo.
        Assert.True(FileSystemUtil.IsSameVolume(from, to, _ => null));
        Assert.False(FileSystemUtil.IsSameVolume(@"C:\a", @"D:\a", _ => 1u));
    }

    [Fact]
    public void IsSameVolume_RealFoldersOnSameDrive_ReadsSameVolumeSerial()
    {
        var a = _temp.Combine("a");
        Directory.CreateDirectory(a);

        var serialTemp = FileSystemUtil.TryGetVolumeSerial(_temp.Path);
        Assert.NotNull(serialTemp);
        Assert.Equal(serialTemp, FileSystemUtil.TryGetVolumeSerial(Path.GetPathRoot(_temp.Path)!));
        Assert.Null(FileSystemUtil.TryGetVolumeSerial(_temp.Combine("no-existe")));
        Assert.True(FileSystemUtil.IsSameVolume(Path.Combine(a, "Coop2D"), _temp.Combine("no", "existe", "aun")));
    }

    [Fact]
    public void IsSameVolume_ThroughJunctionOnSameVolume_IsTrue()
    {
        var target = _temp.Combine("destino-real");
        Directory.CreateDirectory(target);
        var link = _temp.Combine("enlace");
        if (!TryCreateJunction(link, target))
        {
            return;
        }
        try
        {
            Assert.Equal(FileSystemUtil.TryGetVolumeSerial(target), FileSystemUtil.TryGetVolumeSerial(link));
            Assert.True(FileSystemUtil.IsSameVolume(Path.Combine(link, "Coop2D"), _temp.Combine("origen", "Coop2D")));
        }
        finally
        {
            // Solo el enlace (nunca recursivo a través de él).
            Directory.Delete(link);
        }
    }

    [Fact]
    public void IsNotSameDevice_RecognizesTheHResultDirectOrWrapped()
    {
        var notSame = new IOException("x", FileSystemUtil.HResultNotSameDevice);

        Assert.True(FileSystemUtil.IsNotSameDevice(notSame));
        Assert.True(FileSystemUtil.IsNotSameDevice(new LauncherException("y", notSame)));
        Assert.False(FileSystemUtil.IsNotSameDevice(new IOException("z")));
        Assert.False(FileSystemUtil.IsNotSameDevice(null));
    }

    [Fact]
    public void IsAntivirusBlock_AndIsSharingViolation_UseHResults()
    {
        Assert.True(FileSystemUtil.IsAntivirusBlock(new IOException("v", unchecked((int)0x800700E1))));
        Assert.True(FileSystemUtil.IsAntivirusBlock(new IOException("v", unchecked((int)0x800700E2))));
        Assert.False(FileSystemUtil.IsAntivirusBlock(new UnauthorizedAccessException()));
        Assert.True(FileSystemUtil.IsSharingViolation(new IOException("s", unchecked((int)0x80070020))));
        Assert.False(FileSystemUtil.IsSharingViolation(new IOException("s")));
    }

    [Fact]
    public void GetAntivirusExclusionFolder_PrefersInstallRootAboveLauncherFolder()
    {
        var root = _temp.Combine("Juegos", "Coop2D");
        var download = Path.Combine(root, ".launcher", "downloads", "x.zip");
        var staging = Path.Combine(root, ".launcher", "staging", "abcd1234");

        Assert.Equal(root, FileSystemUtil.GetAntivirusExclusionFolder(download, isDirectory: false));
        Assert.Equal(root, FileSystemUtil.GetAntivirusExclusionFolder(staging, isDirectory: true));
        Assert.Equal(_temp.Combine("otra"), FileSystemUtil.GetAntivirusExclusionFolder(_temp.Combine("otra", "PalitoLauncher.exe.new"), isDirectory: false));
    }

    // ───────────────────────── Escritura atómica ─────────────────────────

    [Fact]
    public void WriteAllTextAtomic_CreatesFolder_WritesUtf8WithoutBom_AndOverwritesReadOnly()
    {
        var path = _temp.Combine("sub", "dir", "settings.json");

        FileSystemUtil.WriteAllTextAtomic(path, "{ \"año\": 1 }");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "No debe llevar BOM");
        Assert.Equal("{ \"año\": 1 }", Encoding.UTF8.GetString(bytes));

        File.SetAttributes(path, FileAttributes.ReadOnly);
        FileSystemUtil.WriteAllTextAtomic(path, "nuevo");

        Assert.Equal("nuevo", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_temp.Combine("sub", "dir"), "*.tmp"));
    }

    [Fact]
    public void WriteAllTextAtomic_DestinationLocked_ThrowsAndLeavesNoTemp()
    {
        var path = _temp.WriteFile("locked.json", "viejo");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.ThrowsAny<Exception>(() => FileSystemUtil.WriteAllTextAtomic(path, "nuevo"));
            Assert.True(ex is IOException or UnauthorizedAccessException, ex.GetType().FullName);
        }

        Assert.Equal("viejo", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
    }

    // ───────────────────────── Borrado / limpieza ─────────────────────────

    [Fact]
    public void CleanupStaleTempFiles_DeletesOnlyOldTmpFilesInThatFolder()
    {
        var old = _temp.WriteFile("old.tmp", "x");
        var fresh = _temp.WriteFile("fresh.tmp", "x");
        var other = _temp.WriteFile("old.json", "x");
        var nested = _temp.WriteFile(Path.Combine("sub", "nested.tmp"), "x");
        var longExtension = _temp.WriteFile("old.tmpx", "x");
        var past = DateTime.UtcNow.AddHours(-3);
        foreach (var file in new[] { old, other, nested, longExtension })
        {
            File.SetLastWriteTimeUtc(file, past);
        }
        File.SetAttributes(old, FileAttributes.ReadOnly | FileAttributes.Hidden);

        FileSystemUtil.CleanupStaleTempFiles(_temp.Path, TimeSpan.FromHours(1));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
        Assert.True(File.Exists(longExtension));
    }

    [Fact]
    public void CleanupStaleTempFiles_MissingFolder_DoesNotThrow()
    {
        FileSystemUtil.CleanupStaleTempFiles(_temp.Combine("no-existe"), TimeSpan.Zero);
    }

    [Fact]
    public void TryDeleteDirectory_RemovesReadOnlyTree()
    {
        var root = _temp.Combine("tree");
        var file = _temp.WriteFile(Path.Combine("tree", "a", "b", "file.txt"), "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        new DirectoryInfo(_temp.Combine("tree", "a")).Attributes |= FileAttributes.ReadOnly;

        Assert.True(FileSystemUtil.TryDeleteDirectory(root));
        Assert.False(Directory.Exists(root));
        Assert.True(FileSystemUtil.TryDeleteDirectory(root));
        Assert.False(FileSystemUtil.TryDeleteDirectory(""));
    }

    [Fact]
    public void TryDeleteDirectory_LockedFile_ReturnsFalseWithoutThrowing()
    {
        var root = _temp.Combine("locked");
        var file = _temp.WriteFile(Path.Combine("locked", "in-use.bin"), "x");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(FileSystemUtil.TryDeleteDirectory(root, attempts: 2, delayMs: 10));
        }
        Assert.True(FileSystemUtil.TryDeleteDirectory(root));
    }

    [Fact]
    public void TryDeleteFile_RemovesReadOnlyFile_AndHandlesMissingOrLocked()
    {
        var file = _temp.WriteFile("ro.txt", "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(FileSystemUtil.TryDeleteFile(file));
        Assert.False(File.Exists(file));
        Assert.True(FileSystemUtil.TryDeleteFile(file));

        var locked = _temp.WriteFile("locked.txt", "x");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(FileSystemUtil.TryDeleteFile(locked));
        }
    }

    [Fact]
    public void GetDirectorySize_SumsNestedAndHiddenFiles()
    {
        _temp.WriteFile("a.bin", new byte[100]);
        var hidden = _temp.WriteFile(Path.Combine(".launcher", "b.bin"), new byte[250]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        new DirectoryInfo(_temp.Combine(".launcher")).Attributes |= FileAttributes.Hidden;
        _temp.WriteFile(Path.Combine("x", "y", "c.bin"), new byte[1000]);

        Assert.Equal(1350, FileSystemUtil.GetDirectorySize(_temp.Path));
        Assert.Equal(0, FileSystemUtil.GetDirectorySize(_temp.Combine("no-existe")));
    }

    // ───────────────────────── Mover ─────────────────────────

    [Fact]
    public void MoveDirectoryWithRetry_MovesAndCreatesDestinationParent()
    {
        _temp.WriteFile(Path.Combine("src", "game", "Coop2D.exe"), "MZ");
        var dest = _temp.Combine("work", "old", "20261019120000");

        FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("src", "game"), dest);

        Assert.True(File.Exists(Path.Combine(dest, "Coop2D.exe")));
        Assert.False(Directory.Exists(_temp.Combine("src", "game")));
    }

    [Fact]
    public void MoveDirectoryWithRetry_LockedFileInside_ThrowsLauncherExceptionWithInner()
    {
        var file = _temp.WriteFile(Path.Combine("busy", "Coop2D.exe"), "MZ");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<LauncherException>(
                () => FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("busy"), _temp.Combine("moved"), attempts: 2, delayMs: 10));

            Assert.Contains("Cierra el juego", ex.Message);
            Assert.NotNull(ex.InnerException);
        }
        Assert.True(Directory.Exists(_temp.Combine("busy")));
    }

    [Fact]
    public void MoveDirectoryWithRetry_MissingSourceOrExistingDestination_FailsFast()
    {
        Directory.CreateDirectory(_temp.Combine("a"));
        Directory.CreateDirectory(_temp.Combine("b"));

        Assert.Throws<LauncherException>(() => FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("no-existe"), _temp.Combine("x")));
        Assert.Throws<LauncherException>(() => FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("a"), _temp.Combine("b")));
        Assert.True(Directory.Exists(_temp.Combine("a")));
    }

    [Fact]
    public void MoveDirectoryWithRetry_NotSameDevice_ThrowsUnwrappedIOExceptionAtOnce()
    {
        _temp.WriteFile(Path.Combine("src", "Coop2D.exe"), "MZ");
        var calls = 0;
        void CrossVolumeMove(string a, string b)
        {
            calls++;
            throw new IOException("No se puede mover el archivo a otra unidad de disco.", FileSystemUtil.HResultNotSameDevice);
        }

        var ex = Assert.Throws<IOException>(
            () => FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("src"), _temp.Combine("dst"), 10, 500, CrossVolumeMove));

        Assert.Equal(FileSystemUtil.HResultNotSameDevice, ex.HResult);
        Assert.True(FileSystemUtil.IsNotSameDevice(ex));
        Assert.Equal(1, calls);
        Assert.True(File.Exists(_temp.Combine("src", "Coop2D.exe")));
    }

    [Fact]
    public void MoveDirectoryWithRetry_DifferentDrive_ThrowsNotSameDeviceWithoutTouchingAnything()
    {
        _temp.WriteFile(Path.Combine("src", "Coop2D.exe"), "MZ");
        var otherLetter = char.ToUpperInvariant(_temp.Path[0]) == 'C' ? 'D' : 'C';
        var dest = $@"{otherLetter}:\palito-tests-{Guid.NewGuid():N}\Coop2D";
        var calls = 0;

        var ex = Assert.Throws<IOException>(
            () => FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("src"), dest, 10, 500, (_, _) => calls++));

        Assert.True(FileSystemUtil.IsNotSameDevice(ex));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(Path.GetDirectoryName(dest)));
        Assert.True(Directory.Exists(_temp.Combine("src")));
    }

    [Fact]
    public void MoveDirectoryWithRetry_OtherTransientErrors_AreStillRetried()
    {
        _temp.WriteFile(Path.Combine("src", "Coop2D.exe"), "MZ");
        var calls = 0;
        void FlakyMove(string a, string b)
        {
            if (++calls < 3)
            {
                throw new IOException("En uso (simulado).", unchecked((int)0x80070020));
            }
            Directory.Move(a, b);
        }

        FileSystemUtil.MoveDirectoryWithRetry(_temp.Combine("src"), _temp.Combine("dst"), 5, 1, FlakyMove);

        Assert.Equal(3, calls);
        Assert.True(File.Exists(_temp.Combine("dst", "Coop2D.exe")));
    }

    [Fact]
    public void MoveFileWithRetry_NotSameDevice_ThrowsUnwrappedIOExceptionAtOnce()
    {
        var source = _temp.WriteFile("a.bin", "a");
        var calls = 0;

        var ex = Assert.Throws<IOException>(() => FileSystemUtil.MoveFileWithRetry(source, _temp.Combine("b.bin"), true, 10, 500,
            (_, _, _) =>
            {
                calls++;
                throw new IOException("Otra unidad (simulado).", FileSystemUtil.HResultNotSameDevice);
            }));

        Assert.True(FileSystemUtil.IsNotSameDevice(ex));
        Assert.Equal(1, calls);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void MoveFileWithRetry_OverwritesReadOnlyDestination()
    {
        var source = _temp.WriteFile("new.exe", "nuevo");
        var dest = _temp.WriteFile("app.exe", "viejo");
        File.SetAttributes(dest, FileAttributes.ReadOnly);

        FileSystemUtil.MoveFileWithRetry(source, dest, overwrite: true);

        Assert.Equal("nuevo", File.ReadAllText(dest));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void MoveFileWithRetry_NoOverwriteOrLocked_ThrowsLauncherException()
    {
        var source = _temp.WriteFile("a.txt", "a");
        var dest = _temp.WriteFile("b.txt", "b");

        Assert.Throws<LauncherException>(() => FileSystemUtil.MoveFileWithRetry(source, dest, overwrite: false));

        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<LauncherException>(
                () => FileSystemUtil.MoveFileWithRetry(source, _temp.Combine("c.txt"), overwrite: true, attempts: 2, delayMs: 10));
            Assert.NotNull(ex.InnerException);
        }
        Assert.Equal("a", File.ReadAllText(source));
    }

    [Fact]
    public async Task CopyDirectoryAsync_CopiesTreeSkipsLockFileAndReportsProgress()
    {
        _temp.WriteFile(Path.Combine("src", "game", "Coop2D.exe"), new byte[5000]);
        _temp.WriteFile(Path.Combine("src", "game", "Coop2D_Data", "app.info"), "PalitoGames\nCoop2D");
        var hidden = _temp.WriteFile(Path.Combine("src", ".launcher", "downloads", "x.part"), new byte[300]);
        _temp.WriteFile(Path.Combine("src", ".launcher", "operation.lock"), "lock");
        new DirectoryInfo(_temp.Combine("src", ".launcher")).Attributes |= FileAttributes.Hidden;
        Directory.CreateDirectory(_temp.Combine("src", "emptydir"));
        var stamp = new DateTime(2024, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_temp.Combine("src", "game", "Coop2D.exe"), stamp);
        var progress = new SyncProgress<(long Done, long Total)>();
        var dest = _temp.Combine("dst.moving");

        await FileSystemUtil.CopyDirectoryAsync(_temp.Combine("src"), dest, progress, CancellationToken.None);

        Assert.Equal(5000, new FileInfo(Path.Combine(dest, "game", "Coop2D.exe")).Length);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dest, "game", "Coop2D.exe")));
        Assert.Equal("PalitoGames\nCoop2D", File.ReadAllText(Path.Combine(dest, "game", "Coop2D_Data", "app.info")));
        Assert.True(File.Exists(Path.Combine(dest, ".launcher", "downloads", "x.part")));
        Assert.False(File.Exists(Path.Combine(dest, ".launcher", "operation.lock")));
        Assert.True(Directory.Exists(Path.Combine(dest, "emptydir")));
        Assert.True(new DirectoryInfo(Path.Combine(dest, ".launcher")).Attributes.HasFlag(FileAttributes.Hidden));
        var expectedTotal = 5000 + "PalitoGames\nCoop2D"u8.Length + 300;
        var last = progress.Values.Last();
        Assert.Equal(expectedTotal, last.Total);
        Assert.Equal(expectedTotal, last.Done);
        Assert.True(File.Exists(hidden));
    }

    [Fact]
    public async Task CopyDirectoryAsync_Cancelled_ThrowsOperationCanceled()
    {
        _temp.WriteFile(Path.Combine("src", "a.bin"), new byte[1000]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileSystemUtil.CopyDirectoryAsync(_temp.Combine("src"), _temp.Combine("dst"), null, cts.Token));
    }

    // ───────────────────────── Unidades / espacio / escritura ─────────────────────────

    [Fact]
    public void IsDirectoryWritable_CreatesFolderAndLeavesNoProbe()
    {
        var dir = _temp.Combine("nueva", "carpeta");

        Assert.True(FileSystemUtil.IsDirectoryWritable(dir));
        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public void IsDirectoryWritable_InvalidPath_ReturnsFalse()
    {
        Assert.False(FileSystemUtil.IsDirectoryWritable(""));
        Assert.False(FileSystemUtil.IsDirectoryWritable(_temp.Combine("inva|lido")));
        var file = _temp.WriteFile("soy-un-archivo", "x");
        Assert.False(FileSystemUtil.IsDirectoryWritable(file));
    }

    [Fact]
    public void IsDriveReady_DetectsExistingAndMissingDrives()
    {
        Assert.True(FileSystemUtil.IsDriveReady(_temp.Combine("no", "existe", "aun")));
        Assert.False(FileSystemUtil.IsDriveReady(@"relativa\carpeta"));
        Assert.False(FileSystemUtil.IsDriveReady(""));
        var missing = FindMissingDriveLetter();
        if (missing is not null)
        {
            Assert.False(FileSystemUtil.IsDriveReady($@"{missing}:\Juegos\Coop2D"));
        }
    }

    [Fact]
    public void GetAvailableFreeSpace_WalksUpToExistingAncestor()
    {
        var free = FileSystemUtil.GetAvailableFreeSpace(_temp.Combine("no", "existe", "todavia"));

        Assert.InRange(free, 1, long.MaxValue - 1);
    }

    [Fact]
    public void GetAvailableFreeSpace_MissingDrive_ReturnsMaxValue()
    {
        var missing = FindMissingDriveLetter();
        if (missing is not null)
        {
            Assert.Equal(long.MaxValue, FileSystemUtil.GetAvailableFreeSpace($@"{missing}:\Juegos"));
        }
    }

    // ───────────────────────── CheckInstallLocation ─────────────────────────

    [Fact]
    public void CheckInstallLocation_NormalFolder_IsOk()
    {
        var check = FileSystemUtil.CheckInstallLocation(_temp.Combine("Juegos", "Coop2D"), @"C:\Users\x\AppData\Local\Programs\PalitoLauncher\PalitoLauncher.exe");

        Assert.False(check.IsBlocked, check.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relativa\Coop2D")]
    [InlineData(@"C:Coop2D")]
    [InlineData(@"\\servidor\recurso\Coop2D")]
    [InlineData(@"\\?\C:\Juegos\Coop2D")]
    [InlineData(@"C:\Juegos\Co<op")]
    public void CheckInstallLocation_InvalidOrNetworkPaths_AreBlocked(string path)
    {
        var check = FileSystemUtil.CheckInstallLocation(path, null);

        Assert.True(check.IsBlocked);
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
    }

    [Fact]
    public void CheckInstallLocation_DriveRoot_IsBlocked()
    {
        var root = Path.GetPathRoot(_temp.Path)!;

        Assert.True(FileSystemUtil.CheckInstallLocation(root, null).IsBlocked);
        Assert.True(FileSystemUtil.CheckInstallLocation(root.TrimEnd('\\'), null).IsBlocked);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows, "Coop2D")]
    [InlineData(Environment.SpecialFolder.ProgramFiles, "Coop2D")]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86, "Coop2D")]
    [InlineData(Environment.SpecialFolder.UserProfile, "")]
    [InlineData(Environment.SpecialFolder.Desktop, "")]
    [InlineData(Environment.SpecialFolder.MyDocuments, "")]
    public void CheckInstallLocation_SystemAndPersonalFolders_AreBlocked(Environment.SpecialFolder folder, string child)
    {
        var basePath = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(basePath))
        {
            return;
        }

        var path = child.Length == 0 ? basePath : Path.Combine(basePath, child);
        var check = FileSystemUtil.CheckInstallLocation(path, null);

        Assert.True(check.IsBlocked, $"{path} debería estar bloqueada");
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
    }

    [Fact]
    public void CheckInstallLocation_SubfolderOfProfile_IsAllowed()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Juegos", "Coop2D");

        Assert.False(FileSystemUtil.CheckInstallLocation(path, null).IsBlocked);
    }

    [Fact]
    public void CheckInstallLocation_LauncherInsideFolder_IsBlocked()
    {
        var root = _temp.Combine("Coop2D");

        var check = FileSystemUtil.CheckInstallLocation(root, Path.Combine(root, "PalitoLauncher.exe"));

        Assert.True(check.IsBlocked);
        Assert.False(FileSystemUtil.CheckInstallLocation(root, _temp.Combine("Coop2D2", "PalitoLauncher.exe")).IsBlocked);
    }

    [Fact]
    public void CheckInstallLocation_MissingDrive_IsBlocked()
    {
        var missing = FindMissingDriveLetter();
        if (missing is null)
        {
            return;
        }

        var check = FileSystemUtil.CheckInstallLocation($@"{missing}:\Juegos\Coop2D", null);

        Assert.True(check.IsBlocked);
    }

    [Fact]
    public void CheckInstallLocation_InsideOneDrive_WarnsWithoutBlocking()
    {
        const string variable = "OneDriveCommercial";
        var previous = Environment.GetEnvironmentVariable(variable);
        var oneDrive = _temp.Combine("OneDrive - Empresa");
        try
        {
            Environment.SetEnvironmentVariable(variable, oneDrive);

            var check = FileSystemUtil.CheckInstallLocation(Path.Combine(oneDrive, "Juegos", "Coop2D"), null);

            Assert.False(check.IsBlocked);
            Assert.True(check.IsWarning);
            Assert.Contains("OneDrive", check.Reason);
            Assert.False(FileSystemUtil.CheckInstallLocation(_temp.Combine("Local", "Coop2D"), null).IsWarning);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    /// <summary>Crea una unión (mklink /J, no requiere administrador). False si no se pudo.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static char? FindMissingDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'H'; letter--)
        {
            if (!used.Contains(letter))
            {
                return letter;
            }
        }
        return null;
    }
}
