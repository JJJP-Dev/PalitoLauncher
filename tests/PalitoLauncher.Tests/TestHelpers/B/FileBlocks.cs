using System.Security.AccessControl;
using System.Security.Principal;

namespace PalitoLauncher.Tests.TestHelpers.B;

/// <summary>Simula archivos bloqueados como lo haría un antivirus (sin tocar nada fuera de la carpeta temporal del test).</summary>
public static class FileBlocks
{
    /// <summary>
    /// Niega la lectura del contenido al usuario actual (abrir para leer → UnauthorizedAccessException, como cuando el
    /// antivirus bloquea un archivo). Dispose quita la regla.
    /// </summary>
    public static IDisposable DenyRead(string path)
    {
        var info = new FileInfo(path);
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return new Restore(() =>
        {
            var current = info.GetAccessControl();
            current.RemoveAccessRule(rule);
            info.SetAccessControl(current);
        });
    }

    /// <summary>Abre el archivo en exclusiva (otro programa usándolo → IOException ERROR_SHARING_VIOLATION).</summary>
    public static FileStream LockExclusive(string path, FileMode mode = FileMode.Open)
        => new(path, mode, FileAccess.ReadWrite, FileShare.None);

    /// <summary>Una letra de unidad que no existe en este PC (o null si están todas usadas).</summary>
    public static char? FindMissingDriveLetter()
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

    private sealed class Restore(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                undo();
            }
        }
    }
}
