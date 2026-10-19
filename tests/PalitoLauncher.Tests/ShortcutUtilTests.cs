using System.Text;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.F;

namespace PalitoLauncher.Tests;

public class ShortcutUtilTests
{
    private static readonly string Notepad =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    private static bool ContainsUtf16(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

    [Fact]
    public void CreateShortcut_CreatesLnkWithShellLinkHeader()
    {
        using var tmp = new TempDir();
        var lnk = tmp.Combine("sub", "carpeta nueva", "Prueba.lnk");

        ShortcutUtil.CreateShortcut(lnk, Notepad, "--hola mundo", "Descripción de prueba");

        Assert.True(File.Exists(lnk));
        var bytes = File.ReadAllBytes(lnk);
        Assert.True(bytes.Length > 76);
        Assert.Equal(new byte[] { 0x4C, 0x00, 0x00, 0x00 }, bytes[..4]);
        // Argumentos, descripción y carpeta de trabajo se guardan como cadenas UTF-16.
        Assert.True(ContainsUtf16(bytes, "--hola mundo"));
        Assert.True(ContainsUtf16(bytes, "Descripción de prueba"));
        Assert.True(ContainsUtf16(bytes, Path.GetDirectoryName(Notepad)!));
    }

    [Fact]
    public void CreateShortcut_OverwritesExistingShortcut()
    {
        using var tmp = new TempDir();
        var lnk = tmp.Combine("Prueba.lnk");

        ShortcutUtil.CreateShortcut(lnk, Notepad, "--primero");
        File.SetAttributes(lnk, FileAttributes.ReadOnly);
        ShortcutUtil.CreateShortcut(lnk, Notepad, "--segundo");

        var bytes = File.ReadAllBytes(lnk);
        Assert.True(ContainsUtf16(bytes, "--segundo"));
        Assert.False(ContainsUtf16(bytes, "--primero"));
    }

    [Fact]
    public void CreateShortcut_EmptyTarget_ThrowsLauncherException()
    {
        using var tmp = new TempDir();
        Assert.Throws<LauncherException>(() => ShortcutUtil.CreateShortcut(tmp.Combine("x.lnk"), ""));
        Assert.Throws<LauncherException>(() => ShortcutUtil.CreateShortcut("", Notepad));
    }

    [Fact]
    public void CreateShortcut_FolderCannotBeCreated_ThrowsLauncherException()
    {
        using var tmp = new TempDir();
        // La "carpeta" del acceso directo es en realidad un archivo.
        var file = tmp.WriteFile("soy-un-archivo.txt", "x");
        var lnk = Path.Combine(file, "x.lnk");

        Assert.Throws<LauncherException>(() => ShortcutUtil.CreateShortcut(lnk, Notepad));
    }
}
