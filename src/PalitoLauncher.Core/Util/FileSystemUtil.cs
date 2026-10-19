using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Core.Util;

/// <summary>Resultado de validar una carpeta de instalación.</summary>
public sealed class InstallLocationCheck
{
    public static readonly InstallLocationCheck Ok = new();

    /// <summary>No se permite instalar ahí (el botón Instalar se deshabilita).</summary>
    public bool IsBlocked { get; init; }

    /// <summary>Se permite pero conviene avisar (p.ej. carpeta sincronizada con OneDrive).</summary>
    public bool IsWarning { get; init; }

    /// <summary>Motivo en español (null si Ok).</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Utilidades de sistema de archivos tolerantes a los bloqueos temporales típicos de Windows
/// (antivirus escaneando archivos recién extraídos, Explorador con la carpeta abierta, OneDrive subiendo, etc.).
/// </summary>
public static class FileSystemUtil
{
    private const int CopyBufferSize = 81920;
    private const string OperationLockFileName = "operation.lock";

    // HRESULT de disco lleno (ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL).
    private const int HResultDiskFull = unchecked((int)0x80070070);
    private const int HResultHandleDiskFull = unchecked((int)0x80070027);

    /// <summary>HRESULT de ERROR_NOT_SAME_DEVICE: un renombrado no puede cruzar de volumen.</summary>
    internal const int HResultNotSameDevice = unchecked((int)0x80070011);

    // HRESULT del antivirus (ERROR_VIRUS_INFECTED / ERROR_VIRUS_DELETED).
    private const int HResultVirusInfected = unchecked((int)0x800700E1);
    private const int HResultVirusDeleted = unchecked((int)0x800700E2);

    // HRESULT de archivo en uso por otro proceso (ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION).
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Directory.Move con reintentos ante IOException/UnauthorizedAccessException (por defecto 10 intentos, 500 ms entre ellos).
    /// Si tras todos los intentos sigue fallando, lanza LauncherException ("No se pudo mover … Cierra el juego o cualquier
    /// ventana que tenga abierta esa carpeta y vuelve a intentarlo.") con la excepción original como InnerException.
    /// Sin reintentos (se informa enseguida): origen inexistente o destino ya existente → LauncherException.
    /// DISTINTO VOLUMEN: si <see cref="IsSameVolume(string, string)"/> devuelve false, o Directory.Move falla con ERROR_NOT_SAME_DEVICE
    /// (p.ej. el destino está bajo una unión o un punto de montaje que apunta a otra unidad), lanza ENSEGUIDA y sin reintentar
    /// una <see cref="IOException"/> NO envuelta en LauncherException con HResult == 0x80070011 (ERROR_NOT_SAME_DEVICE)
    /// (<see cref="HResultNotSameDevice"/>). El origen queda intacto y no se crea el destino (solo, quizá, su carpeta padre).
    /// <see cref="IsNotSameDevice"/> la reconoce: el llamador debe copiar (<see cref="CopyDirectoryAsync"/>) en su lugar.
    /// </summary>
    public static void MoveDirectoryWithRetry(string sourceDir, string destDir, int attempts = 10, int delayMs = 500)
        => MoveDirectoryWithRetry(sourceDir, destDir, attempts, delayMs, Directory.Move);

    /// <summary>Igual que la pública, con la operación de mover inyectable (tests).</summary>
    internal static void MoveDirectoryWithRetry(string sourceDir, string destDir, int attempts, int delayMs, Action<string, string> move)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        ArgumentNullException.ThrowIfNull(move);

        var source = NormalizeOrSelf(sourceDir);
        var dest = NormalizeOrSelf(destDir);
        var caseOnlyRename = string.Equals(source, dest, StringComparison.OrdinalIgnoreCase);

        // Errores que no se arreglan reintentando: se informa enseguida (sin esperar 5 s).
        if (!Directory.Exists(source))
        {
            throw new LauncherException($"No se encontró la carpeta «{source}».",
                new DirectoryNotFoundException(source));
        }
        if (!caseOnlyRename && (Directory.Exists(dest) || File.Exists(dest)))
        {
            throw new LauncherException($"No se pudo mover «{source}» porque «{dest}» ya existe.",
                new IOException($"El destino ya existe: {dest}"));
        }
        if (!IsSameVolume(source, dest))
        {
            Log.Info($"No se puede renombrar entre volúmenes distintos: {source} → {dest}");
            throw new IOException($"No se pudo mover «{source}» a «{dest}»: están en unidades distintas.", HResultNotSameDevice);
        }

        attempts = Math.Max(1, attempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                EnsureParentDirectory(dest);
                move(source, dest);
                if (attempt > 1)
                {
                    Log.Info($"Carpeta movida al intento {attempt}: {source} → {dest}");
                }
                return;
            }
            catch (IOException ex) when (ex.HResult == HResultNotSameDevice)
            {
                // Reintentar no sirve: el llamador debe copiar.
                Log.Info($"Directory.Move no puede cruzar de volumen (ERROR_NOT_SAME_DEVICE): {source} → {dest}");
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                {
                    Log.Warn($"No se pudo mover la carpeta tras {attempt} intentos: {source} → {dest}", ex);
                    throw new LauncherException(
                        $"No se pudo mover «{source}». Cierra el juego o cualquier ventana que tenga abierta esa carpeta y vuelve a intentarlo.",
                        ex);
                }
                Log.Debug($"Intento {attempt} de mover carpeta fallido ({ex.GetType().Name}: {ex.Message}); reintentando.");
                Thread.Sleep(Math.Max(0, delayMs));
            }
        }
    }

    /// <summary>
    /// File.Move (con overwrite opcional, quitando solo lectura del destino) con los mismos reintentos y la misma
    /// LauncherException final que <see cref="MoveDirectoryWithRetry(string, string, int, int)"/>. Un fallo con
    /// ERROR_NOT_SAME_DEVICE (0x80070011) no se reintenta: se relanza enseguida la IOException original, sin envolver.
    /// </summary>
    public static void MoveFileWithRetry(string sourceFile, string destFile, bool overwrite, int attempts = 10, int delayMs = 500)
        => MoveFileWithRetry(sourceFile, destFile, overwrite, attempts, delayMs, File.Move);

    /// <summary>Igual que la pública, con la operación de mover inyectable (tests).</summary>
    internal static void MoveFileWithRetry(string sourceFile, string destFile, bool overwrite, int attempts, int delayMs,
        Action<string, string, bool> move)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destFile);
        ArgumentNullException.ThrowIfNull(move);

        var source = NormalizeOrSelf(sourceFile);
        var dest = NormalizeOrSelf(destFile);

        if (!File.Exists(source))
        {
            throw new LauncherException($"No se encontró el archivo «{source}».", new FileNotFoundException(source));
        }
        if (!overwrite && File.Exists(dest) && !string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException($"No se pudo mover «{source}» porque «{dest}» ya existe.",
                new IOException($"El destino ya existe: {dest}"));
        }

        attempts = Math.Max(1, attempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                EnsureParentDirectory(dest);
                if (overwrite)
                {
                    ClearReadOnly(dest);
                }
                move(source, dest, overwrite);
                if (attempt > 1)
                {
                    Log.Info($"Archivo movido al intento {attempt}: {source} → {dest}");
                }
                return;
            }
            catch (IOException ex) when (ex.HResult == HResultNotSameDevice)
            {
                Log.Info($"File.Move no puede cruzar de volumen (ERROR_NOT_SAME_DEVICE): {source} → {dest}");
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                {
                    Log.Warn($"No se pudo mover el archivo tras {attempt} intentos: {source} → {dest}", ex);
                    throw new LauncherException(
                        $"No se pudo mover «{source}». Cierra el juego o cualquier ventana que tenga abierta esa carpeta y vuelve a intentarlo.",
                        ex);
                }
                Log.Debug($"Intento {attempt} de mover archivo fallido ({ex.GetType().Name}: {ex.Message}); reintentando.");
                Thread.Sleep(Math.Max(0, delayMs));
            }
        }
    }

    /// <summary>
    /// Borra una carpeta recursivamente (quitando atributos de solo lectura) con reintentos.
    /// Devuelve true si la carpeta ya no existe al terminar. Nunca lanza.
    /// </summary>
    public static bool TryDeleteDirectory(string path, int attempts = 5, int delayMs = 400)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            attempts = Math.Max(1, attempts);
            Exception? last = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                try
                {
                    ClearReadOnlyRecursive(path);
                    Directory.Delete(path, recursive: true);
                    return true;
                }
                catch (DirectoryNotFoundException)
                {
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    if (attempt < attempts)
                    {
                        Thread.Sleep(Math.Max(0, delayMs));
                    }
                }
            }

            var gone = !Directory.Exists(path);
            if (!gone)
            {
                Log.Warn($"No se pudo borrar la carpeta {path} tras {attempts} intentos.", last);
            }
            return gone;
        }
        catch (Exception ex)
        {
            Log.Warn($"Error inesperado al borrar la carpeta {path}.", ex);
            return SafeNotExists(path, isDirectory: true);
        }
    }

    /// <summary>Borra un archivo (quitando solo lectura). True si ya no existe. Nunca lanza.</summary>
    public static bool TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return true;
            }
            ClearReadOnly(path);
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo borrar el archivo {path}.", ex);
            return SafeNotExists(path, isDirectory: false);
        }
    }

    /// <summary>Tamaño total de los archivos de una carpeta (0 si no existe). Ignora archivos inaccesibles.</summary>
    public static long GetDirectorySize(string path)
    {
        long total = 0;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
            {
                return 0;
            }

            foreach (var file in dir.EnumerateFiles("*", AllFilesRecursive()))
            {
                try
                {
                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Archivo que desapareció o no se puede leer: se ignora.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo calcular el tamaño de {path}.", ex);
        }
        return total;
    }

    /// <summary>
    /// Espacio libre (bytes) en la unidad que contendría <paramref name="path"/> (sube hasta el primer ancestro existente).
    /// Devuelve long.MaxValue si no se puede determinar (para no bloquear al usuario por un fallo de consulta).
    /// </summary>
    public static long GetAvailableFreeSpace(string path)
    {
        try
        {
            var existing = FindExistingAncestor(path);
            if (existing is null)
            {
                return long.MaxValue;
            }

            // GetDiskFreeSpaceEx respeta cuotas y carpetas montadas; DriveInfo como alternativa.
            if (GetDiskFreeSpaceEx(EnsureTrailingSeparator(existing), out var freeToCaller, out _, out _))
            {
                return freeToCaller > long.MaxValue ? long.MaxValue : (long)freeToCaller;
            }

            var root = Path.GetPathRoot(existing);
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    return drive.AvailableFreeSpace;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo consultar el espacio libre de {path}.", ex);
        }
        return long.MaxValue;
    }

    /// <summary>True si la unidad raíz de la ruta existe y está lista (un disco USB desconectado → false). Nunca lanza.</summary>
    public static bool IsDriveReady(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                return false;
            }

            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            if (root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // UNC (\\servidor\recurso\): lista si el recurso responde.
                return Directory.Exists(root);
            }

            var drive = new DriveInfo(root);
            return drive.DriveType != DriveType.NoRootDirectory && drive.IsReady;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Crea la carpeta si hace falta y prueba a escribir/borrar un archivo temporal. Nunca lanza.</summary>
    public static bool IsDirectoryWritable(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string? probe = null;
        try
        {
            Directory.CreateDirectory(path);
            probe = Path.Combine(path, $".palito-write-test-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            {
                fs.WriteByte(0x50);
                fs.Flush(flushToDisk: true);
            }
            File.Delete(probe);
            probe = null;
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"La carpeta {path} no es escribible: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (probe is not null)
            {
                try { File.Delete(probe); }
                catch (Exception) { /* best effort: el archivo de prueba es inofensivo */ }
            }
        }
    }

    /// <summary>
    /// Valida una carpeta de instalación (ya resuelta, p.ej. "D:\Juegos\Coop2D"). Comparaciones con rutas normalizadas
    /// (GetFullPath, sin separador final, OrdinalIgnoreCase) y con IsSubPathOf cuando se indica "dentro de". Nunca lanza.
    /// BLOQUEA (IsBlocked) si: ruta inválida/relativa; raíz de unidad (C:\); dentro de la carpeta de Windows; dentro de
    /// Program Files / Program Files (x86) (requieren administrador); es exactamente el perfil de usuario, el Escritorio o
    /// Documentos (sin subcarpeta); ruta UNC (\\servidor\...) o unidad de red (DriveInfo.DriveType == Network); unidad no
    /// lista; o <paramref name="launcherExePath"/> está dentro de la carpeta (el launcher no puede vivir dentro del juego).
    /// AVISA (IsWarning) si está dentro de una carpeta de OneDrive (variables de entorno OneDrive / OneDriveConsumer /
    /// OneDriveCommercial): "Esta carpeta se sincroniza con OneDrive: ocupará espacio en tu nube y las actualizaciones
    /// pueden fallar. Mejor elige otra."
    /// Las unidades extraíbles (USB) se permiten.
    /// </summary>
    public static InstallLocationCheck CheckInstallLocation(string installRoot, string? launcherExePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot)
                || installRoot.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return Blocked("La ruta no es válida. Elige una carpeta con «Cambiar…».");
            }

            // Rutas de dispositivo (\\?\, \\.\) no se usan para instalar.
            var slashed = installRoot.Trim().Replace('/', '\\');
            if (slashed.StartsWith(@"\\?\", StringComparison.Ordinal) || slashed.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return Blocked("La ruta no es válida. Elige una carpeta con «Cambiar…».");
            }

            string full;
            try
            {
                full = NormalizePath(installRoot);
            }
            catch (Exception)
            {
                return Blocked("La ruta no es válida. Elige una carpeta con «Cambiar…».");
            }

            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return Blocked("No se puede instalar en una carpeta de red. Elige una carpeta de este PC.");
            }

            var root = Path.GetPathRoot(full) ?? "";

            // Caracteres no válidos en algún nombre de carpeta (incluye ':' fuera de la unidad: flujos alternativos NTFS).
            var invalidNameChars = Path.GetInvalidFileNameChars();
            foreach (var segment in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.IndexOfAny(invalidNameChars) >= 0)
                {
                    return Blocked("La ruta no es válida. Elige una carpeta con «Cambiar…».");
                }
            }

            if (string.Equals(full, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return Blocked($"No se puede instalar directamente en la raíz de la unidad ({EnsureTrailingSeparator(full)}). Elige o crea una carpeta dentro.");
            }

            DriveInfo? drive = null;
            try
            {
                drive = new DriveInfo(root);
            }
            catch (ArgumentException)
            {
                return Blocked("La ruta no es válida. Elige una carpeta con «Cambiar…».");
            }

            if (drive.DriveType == DriveType.Network)
            {
                return Blocked("No se puede instalar en una unidad de red. Elige una carpeta de este PC.");
            }
            if (drive.DriveType == DriveType.NoRootDirectory || !drive.IsReady)
            {
                return Blocked($"La unidad {root} no está disponible. Conéctala o elige otra carpeta.");
            }

            var windowsDir = GetFolder(Environment.SpecialFolder.Windows);
            if (windowsDir is not null && IsSubPathOf(full, windowsDir))
            {
                return Blocked("No se puede instalar dentro de la carpeta de Windows. Elige otra carpeta.");
            }

            foreach (var programFiles in new[]
                     {
                         GetFolder(Environment.SpecialFolder.ProgramFiles),
                         GetFolder(Environment.SpecialFolder.ProgramFilesX86),
                         NullIfEmpty(Environment.GetEnvironmentVariable("ProgramW6432")),
                     })
            {
                if (programFiles is not null && IsSubPathOf(full, programFiles))
                {
                    return Blocked("No se puede instalar en «Archivos de programa» (necesita permisos de administrador). Elige otra carpeta, por ejemplo D:\\Juegos.");
                }
            }

            foreach (var personal in new[]
                     {
                         GetFolder(Environment.SpecialFolder.UserProfile),
                         GetFolder(Environment.SpecialFolder.Desktop),
                         GetFolder(Environment.SpecialFolder.MyDocuments),
                     })
            {
                if (personal is not null && PathEquals(full, personal))
                {
                    return Blocked("Elige o crea una subcarpeta en lugar de usar directamente esta carpeta (por ejemplo, una carpeta «Juegos»).");
                }
            }

            if (!string.IsNullOrWhiteSpace(launcherExePath))
            {
                try
                {
                    if (IsSubPathOf(launcherExePath, full))
                    {
                        return Blocked("El launcher está dentro de esa carpeta. Elige otra carpeta para el juego.");
                    }
                }
                catch (Exception)
                {
                    // Ruta del launcher rara: no impide instalar.
                }
            }

            foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                var oneDrive = NullIfEmpty(Environment.GetEnvironmentVariable(variable));
                if (oneDrive is not null && Path.IsPathFullyQualified(oneDrive) && IsSubPathOf(full, oneDrive))
                {
                    return new InstallLocationCheck
                    {
                        IsWarning = true,
                        Reason = "Esta carpeta se sincroniza con OneDrive: ocupará espacio en tu nube y las actualizaciones pueden fallar. Mejor elige otra.",
                    };
                }
            }

            return InstallLocationCheck.Ok;
        }
        catch (Exception ex)
        {
            Log.Warn($"Error inesperado al validar la carpeta de instalación {installRoot}.", ex);
            return Blocked("No se pudo comprobar esa carpeta. Elige otra.");
        }
    }

    /// <summary>
    /// Copia recursiva de carpeta (para mover entre unidades). Reporta (bytesCopiados, bytesTotales). Respeta cancelación.
    /// Omite archivos llamados "operation.lock".
    /// </summary>
    public static Task CopyDirectoryAsync(string sourceDir, string destDir, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        return Task.Run(() => CopyDirectoryCore(sourceDir, destDir, progress, ct), ct);
    }

    /// <summary>
    /// True si un renombrado (Directory.Move) entre ambas rutas se queda en el mismo volumen. Exige el mismo prefijo de unidad
    /// (Path.GetPathRoot, sin distinguir mayúsculas) y, cuando se puede averiguar, el mismo número de serie del volumen real
    /// donde vive cada ruta: el de su carpeta padre (o el primer ancestro que exista), siguiendo uniones y puntos de montaje.
    /// Así "C:\Juegos\Coop2D" con "C:\Juegos" montada o enlazada a D: cuenta como otra unidad. Si no se puede consultar el
    /// volumen (unidad no lista, error) o es una ruta de red, decide solo el prefijo. Nunca lanza.
    /// </summary>
    public static bool IsSameVolume(string pathA, string pathB) => IsSameVolume(pathA, pathB, TryGetVolumeSerial);

    /// <summary>Igual que la pública, con la consulta del número de serie del volumen inyectable (tests).</summary>
    internal static bool IsSameVolume(string pathA, string pathB, Func<string, uint?> volumeSerialOf)
    {
        try
        {
            var fullA = Path.GetFullPath(pathA);
            var fullB = Path.GetFullPath(pathB);
            var rootA = Path.GetPathRoot(fullA);
            var rootB = Path.GetPathRoot(fullB);
            if (string.IsNullOrEmpty(rootA) || string.IsNullOrEmpty(rootB))
            {
                return false;
            }
            if (!string.Equals(rootA.TrimEnd('\\', '/'), rootB.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            // Rutas de red: solo el prefijo (preguntar a un servidor que no responde puede tardar mucho).
            if (rootA.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            var probeA = GetVolumeProbeDirectory(fullA);
            var probeB = GetVolumeProbeDirectory(fullB);
            if (probeA is null || probeB is null)
            {
                return true;
            }
            var serialA = volumeSerialOf(probeA);
            var serialB = volumeSerialOf(probeB);
            return serialA is null || serialB is null || serialA == serialB;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>True si es la IOException de ERROR_NOT_SAME_DEVICE (0x80070011), o la envuelve directamente.</summary>
    internal static bool IsNotSameDevice(Exception? ex)
        => ex is IOException { HResult: HResultNotSameDevice } || ex?.InnerException is IOException { HResult: HResultNotSameDevice };

    /// <summary>True si es un bloqueo del antivirus (IOException con ERROR_VIRUS_INFECTED / ERROR_VIRUS_DELETED).</summary>
    internal static bool IsAntivirusBlock(Exception? ex) => ex is IOException { HResult: HResultVirusInfected or HResultVirusDeleted };

    /// <summary>True si el archivo está en uso por otro proceso (ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION).</summary>
    internal static bool IsSharingViolation(Exception? ex) => ex is IOException { HResult: HResultSharingViolation or HResultLockViolation };

    /// <summary>
    /// Carpeta que conviene excluir en el antivirus para una ruta del launcher: la carpeta de instalación si la ruta está
    /// dentro de su ".launcher"; si no, la carpeta de la propia ruta (o la ruta, si es una carpeta). Nunca lanza.
    /// </summary>
    internal static string GetAntivirusExclusionFolder(string path, bool isDirectory)
    {
        var fallback = path;
        try
        {
            fallback = isDirectory ? path : Path.GetDirectoryName(path) ?? path;
            var current = new DirectoryInfo(fallback);
            while (current is not null)
            {
                if (current.Name.Equals(Install.GameInstaller.WorkFolderName, StringComparison.OrdinalIgnoreCase) && current.Parent is not null)
                {
                    return current.Parent.FullName;
                }
                current = current.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Se usa la ruta tal cual.
        }
        return fallback;
    }

    /// <summary>Número de serie del volumen donde está realmente esa carpeta (sigue uniones y puntos de montaje), o null.</summary>
    internal static uint? TryGetVolumeSerial(string directory)
    {
        try
        {
            using var handle = CreateFile(directory, 0, FileShareReadWriteDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }
            return GetFileInformationByHandle(handle, out var info) ? info.VolumeSerialNumber : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Carpeta existente que decide el volumen de una ruta: su padre (o el primer ancestro existente); la raíz si no tiene padre.</summary>
    private static string? GetVolumeProbeDirectory(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        var parent = Path.GetDirectoryName(trimmed);
        return FindExistingAncestor(parent ?? trimmed);
    }

    /// <summary>¿<paramref name="path"/> es igual o está dentro de <paramref name="parent"/>? (rutas normalizadas, sin distinguir mayúsculas; "C:\Juegos2" NO está dentro de "C:\Juegos").</summary>
    public static bool IsSubPathOf(string path, string parent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            var p = NormalizePath(path);
            var q = NormalizePath(parent);
            if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return p.StartsWith(q + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Formato legible: "512 B", "1.5 KB", "70.8 MB", "1.23 GB" (cultura invariante, 1 decimal; GB con 2).</summary>
    /// <remarks>Unidades decimales (1 KB = 1000 B), como el resto de la UI ("70.8 MB" para 70 782 621 bytes).</remarks>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + FormatBytes(bytes == long.MinValue ? long.MaxValue : -bytes);
        }
        if (bytes < 1000)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        string[] units = ["KB", "MB", "GB", "TB", "PB", "EB"];
        double value = bytes;
        for (var i = 0; i < units.Length; i++)
        {
            value /= 1000.0;
            var decimals = i >= 2 ? 2 : 1;
            var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            // Si al redondear llega a 1000 (p.ej. 999 999 B) se pasa a la unidad siguiente.
            if (rounded < 1000 || i == units.Length - 1)
            {
                var format = decimals == 2 ? "0.00" : "0.0";
                return rounded.ToString(format, CultureInfo.InvariantCulture) + " " + units[i];
            }
        }
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }

    /// <summary>Formato legible de velocidad: "5.3 MB/s" ("—" si es 0 o negativo).</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return "—";
        }
        var bytes = bytesPerSecond >= long.MaxValue ? long.MaxValue : (long)Math.Round(bytesPerSecond);
        return FormatBytes(bytes) + "/s";
    }

    /// <summary>Formato legible de duración restante: "12 s", "3 min 05 s", "1 h 02 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var totalSecondsDouble = duration.TotalSeconds;
        if (double.IsNaN(totalSecondsDouble) || totalSecondsDouble <= 0)
        {
            return "0 s";
        }

        var totalSeconds = (long)Math.Min(Math.Round(totalSecondsDouble, MidpointRounding.AwayFromZero), long.MaxValue / 2);
        if (totalSeconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds} s");
        }
        if (totalSeconds < 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 60} min {totalSeconds % 60:00} s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 3600} h {totalSeconds % 3600 / 60:00} min");
    }

    /// <summary>
    /// Escritura atómica de texto UTF-8 (sin BOM): escribe "&lt;path&gt;.&lt;8 hex aleatorios&gt;.tmp", Flush(true), quita solo
    /// lectura del destino y File.Move(tmp, path, overwrite: true); 3 intentos separados 150 ms ante IOException /
    /// UnauthorizedAccessException; siempre borra el .tmp si falla. Crea la carpeta si no existe. Lanza la última excepción
    /// si fallan todos los intentos.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        contents ??= "";

        var fullPath = Path.GetFullPath(path);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents);
        const int maxAttempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            var tmp = $"{fullPath}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp";
            try
            {
                EnsureParentDirectory(fullPath);
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
                ClearReadOnly(fullPath);
                File.Move(tmp, fullPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DeleteQuietly(tmp);
                if (attempt >= maxAttempts)
                {
                    throw;
                }
                Thread.Sleep(150);
            }
            catch
            {
                DeleteQuietly(tmp);
                throw;
            }
        }
    }

    /// <summary>Borra (best effort) archivos "*.tmp" con más de <paramref name="olderThan"/> en esa carpeta (no recursivo). Nunca lanza.</summary>
    public static void CleanupStaleTempFiles(string directory, TimeSpan olderThan)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                MatchType = MatchType.Simple,
            };
            foreach (var file in dir.EnumerateFiles("*.tmp", options))
            {
                try
                {
                    // Comprobación explícita de la extensión (por si el patrón coincide con nombres cortos 8.3).
                    if (!file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (now - file.LastWriteTimeUtc > olderThan)
                    {
                        TryDeleteFile(file.FullName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"No se pudo revisar el temporal {file.FullName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron limpiar los temporales de {directory}.", ex);
        }
    }

    // ───────────────────────── Internos ─────────────────────────

    /// <summary>True si la excepción es de disco lleno (ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL).</summary>
    internal static bool IsDiskFull(Exception ex) => ex is IOException && ex.HResult is HResultDiskFull or HResultHandleDiskFull;

    /// <summary>GetFullPath sin separador final ("C:\" → "C:").</summary>
    internal static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string NormalizeOrSelf(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static InstallLocationCheck Blocked(string reason) => new() { IsBlocked = true, Reason = reason };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? GetFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return NullIfEmpty(Environment.GetFolderPath(folder));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static string? FindExistingAncestor(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static EnumerationOptions AllFilesRecursive() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // Incluye ocultos/sistema (p.ej. .launcher); no entra en uniones/enlaces simbólicos.
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // No existe o no se puede cambiar: lo resolverá la operación siguiente (o fallará con su propio error).
        }
    }

    private static void ClearReadOnlyRecursive(string directory)
    {
        var root = new DirectoryInfo(directory);
        if ((root.Attributes & FileAttributes.ReadOnly) != 0)
        {
            root.Attributes &= ~FileAttributes.ReadOnly;
        }

        foreach (var info in root.EnumerateFileSystemInfos("*", AllFilesRecursive()))
        {
            try
            {
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    info.Attributes &= ~FileAttributes.ReadOnly;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Se intentará borrar igualmente.
            }
        }
    }

    private static bool SafeNotExists(string path, bool isDirectory)
    {
        try
        {
            return isDirectory ? !Directory.Exists(path) : !File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo borrar el temporal {path}: {ex.Message}");
        }
    }

    private static void CopyDirectoryCore(string sourceDir, string destDir, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var source = new DirectoryInfo(Path.GetFullPath(sourceDir));
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"No existe la carpeta de origen: {source.FullName}");
        }
        var destRoot = Path.GetFullPath(destDir);
        if (IsSubPathOf(destRoot, source.FullName))
        {
            throw new IOException($"El destino {destRoot} está dentro del origen {source.FullName}.");
        }

        // Inventario completo primero (para el total y para no copiar lo que se va creando).
        var options = AllFilesRecursive();
        var directories = source.EnumerateDirectories("*", options).ToList();
        var files = source.EnumerateFiles("*", options)
            .Where(f => !f.Name.Equals(OperationLockFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        long total = 0;
        foreach (var f in files)
        {
            total += f.Length;
        }

        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destRoot);
        foreach (var dir in directories)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destRoot, Path.GetRelativePath(source.FullName, dir.FullName)));
        }

        long done = 0;
        var lastReport = DateTime.UtcNow - ProgressInterval;
        progress?.Report((0, total));
        var buffer = new byte[CopyBufferSize];

        try
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(destRoot, Path.GetRelativePath(source.FullName, file.FullName));
                var targetDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                ClearReadOnly(target);

                using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                           CopyBufferSize, FileOptions.SequentialScan))
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
                           FileOptions.SequentialScan))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        done += read;
                        var now = DateTime.UtcNow;
                        if (progress is not null && now - lastReport >= ProgressInterval)
                        {
                            lastReport = now;
                            progress.Report((done, Math.Max(total, done)));
                        }
                    }
                    output.Flush(flushToDisk: true);
                    TrySetLastWriteTime(output, file.LastWriteTimeUtc);
                }

                // Atributos útiles (oculto/sistema/solo lectura) al final, cuando ya no se escribe más.
                var keep = file.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly);
                if (keep != 0)
                {
                    try { File.SetAttributes(target, File.GetAttributes(target) | keep); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* no es crítico */ }
                }
            }
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new LauncherException(
                $"No hay espacio suficiente en el disco ({Path.GetPathRoot(destRoot)}). Libera espacio y vuelve a intentarlo.", ex);
        }

        // Carpetas ocultas (p.ej. .launcher) se mantienen ocultas en el destino.
        foreach (var dir in directories)
        {
            var keep = dir.Attributes & (FileAttributes.Hidden | FileAttributes.System);
            if (keep == 0)
            {
                continue;
            }
            try
            {
                var target = new DirectoryInfo(Path.Combine(destRoot, Path.GetRelativePath(source.FullName, dir.FullName)));
                target.Attributes |= keep;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No es crítico.
            }
        }

        progress?.Report((done, Math.Max(total, done)));
    }

    private static void TrySetLastWriteTime(FileStream stream, DateTime lastWriteUtc)
    {
        try
        {
            if (lastWriteUtc.Year >= 1980)
            {
                File.SetLastWriteTimeUtc(stream.SafeFileHandle, lastWriteUtc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // La fecha no es crítica.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string lpDirectoryName, out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    private const uint FileShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // necesario para abrir carpetas

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
