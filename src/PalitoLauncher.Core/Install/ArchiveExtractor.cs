using System.Diagnostics;
using System.IO.Compression;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace PalitoLauncher.Core.Install;

/// <summary>Progreso de extracción.</summary>
public readonly record struct ExtractProgress(int EntriesDone, int EntriesTotal, long BytesDone, long BytesTotal)
{
    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : EntriesTotal > 0 ? Math.Clamp((double)EntriesDone / EntriesTotal, 0, 1) : 0;
}

public enum ArchiveFormat { Unknown, Zip, Rar, SevenZip }

/// <summary>Archivo extraído: ruta relativa al destino (con '\') y tamaño descomprimido.</summary>
public readonly record struct ExtractedFile(string RelativePath, long Length);

/// <summary>
/// Extracción de builds (.zip con System.IO.Compression; .rar y .7z con SharpCompress).
/// El formato se detecta por los bytes mágicos, no por la extensión:
///   ZIP "PK\x03\x04" (o "PK\x05\x06" vacío), RAR "Rar!\x1A\x07", 7z "7z\xBC\xAF\x27\x1C".
/// Seguridad: rechaza entradas con rutas absolutas o que salgan del destino (zip-slip) → LauncherException.
/// </summary>
public static class ArchiveExtractor
{
    private const int BufferSize = 81920;
    private const int MaxContentRootDepth = 3;
    private const string MacOsJunkFolder = "__MACOSX";

    internal const string CorruptMessage =
        "El archivo descargado está dañado o su formato no es compatible (se admiten .zip, .rar y .7z). Vuelve a intentarlo; si se repite, avisa al desarrollador.";
    internal const string InvalidPathsMessage = "El archivo contiene rutas no válidas.";
    internal const string PasswordMessage = "El archivo del juego está protegido con contraseña y no se puede instalar. Avisa al desarrollador.";
    internal const string InUseMessage =
        "Otro programa (quizá el antivirus) está usando el archivo descargado. Espera un momento y vuelve a intentarlo.";

    // HRESULT del antivirus (ERROR_VIRUS_INFECTED / ERROR_VIRUS_DELETED).
    private const int HResultVirusInfected = unchecked((int)0x800700E1);
    private const int HResultVirusDeleted = unchecked((int)0x800700E2);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Archivos basura que no cuentan como "contenido" al buscar la raíz del juego.
    private static readonly HashSet<string> JunkFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini",
    };

    private static readonly char[] InvalidSegmentChars = Path.GetInvalidFileNameChars();

    /// <summary>Formato por los bytes mágicos. Nunca lanza: si no se puede leer el archivo devuelve Unknown.</summary>
    public static ArchiveFormat DetectFormat(string archivePath)
    {
        try
        {
            return DetectFormatOrThrow(archivePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warn($"No se pudo leer la cabecera de {archivePath}.", ex);
            return ArchiveFormat.Unknown;
        }
    }

    /// <summary>
    /// Como <see cref="DetectFormat(string)"/>, pero los errores al abrir/leer (IOException, UnauthorizedAccessException…)
    /// se propagan para que el llamador los traduzca (antivirus, archivo que desapareció…). Unknown solo si la cabecera se
    /// leyó y no coincide con ningún formato.
    /// </summary>
    internal static ArchiveFormat DetectFormatOrThrow(string archivePath)
    {
        Span<byte> header = stackalloc byte[8];
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1);
        var read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return DetectFormat(header[..read]);
    }

    /// <summary>Detecta el formato a partir de los primeros bytes.</summary>
    internal static ArchiveFormat DetectFormat(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B
            && ((header[2] == 0x03 && header[3] == 0x04) || (header[2] == 0x05 && header[3] == 0x06)))
        {
            return ArchiveFormat.Zip;
        }
        if (header.StartsWith("Rar!\u001A\u0007"u8))
        {
            return ArchiveFormat.Rar;
        }
        if (header.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
        {
            return ArchiveFormat.SevenZip;
        }
        return ArchiveFormat.Unknown;
    }

    /// <summary>
    /// Suma de tamaños descomprimidos (para comprobar espacio en disco). Formato desconocido o corrupto → LauncherException.
    /// Los errores al leer el archivo se traducen igual que en <see cref="ExtractAsync"/> (antivirus, no encontrado, en uso…).
    /// </summary>
    public static long GetUncompressedSize(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath))
        {
            throw new LauncherException("No se encuentra el archivo descargado. Vuelve a intentarlo.");
        }

        try
        {
            var format = DetectFormatOrThrow(archivePath);
            switch (format)
            {
                case ArchiveFormat.Zip:
                {
                    using var zip = ZipFile.OpenRead(archivePath);
                    long total = 0;
                    foreach (var entry in zip.Entries)
                    {
                        if (!IsZipDirectory(entry))
                        {
                            total += Math.Max(0, entry.Length);
                        }
                    }
                    return total;
                }
                case ArchiveFormat.Rar:
                case ArchiveFormat.SevenZip:
                {
                    using var archive = OpenSharpArchive(archivePath, format);
                    long total = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory)
                        {
                            total += Math.Max(0, entry.Size);
                        }
                    }
                    return total;
                }
                default:
                    Log.Warn($"Formato de archivo desconocido: {archivePath}");
                    throw new LauncherException(CorruptMessage);
            }
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapReadError(ex, archivePath, destinationDir: null);
        }
    }

    /// <summary>
    /// Extrae todo a <paramref name="destinationDir"/> (se crea; si existe debe estar vacío). Copia por bloques
    /// (81920 bytes) comprobando la cancelación, reporta progreso (máx. ~10/s) y corre en un hilo de fondo.
    /// Cada archivo se cierra con FileStream.Flush(true) (datos en disco antes del intercambio; protege ante cortes de luz).
    /// Conserva la fecha de modificación de los archivos. Devuelve la lista de archivos extraídos (para verificarlos después).
    /// Errores → LauncherException en español:
    ///   - corrupto / formato no soportado (InvalidDataException, errores de SharpCompress) → "El archivo descargado está dañado…";
    ///   - disco lleno (HResult 0x80070070 / 0x80070027) → "No hay espacio suficiente en el disco…";
    ///   - antivirus (HResult 0x800700E1 ERROR_VIRUS_INFECTED / 0x800700E2 ERROR_VIRUS_DELETED, o UnauthorizedAccessException
    ///     al crear/escribir un archivo nuevo o al abrir el archivo comprimido, incluida la lectura de su cabecera) →
    ///     "El antivirus bloqueó un archivo del juego (&lt;nombre&gt;). Añade una exclusión para &lt;carpeta&gt; y vuelve a intentarlo.";
    ///   - el archivo comprimido desapareció → "No se encuentra el archivo descargado. Vuelve a intentarlo.";
    ///   - el archivo comprimido está en uso por otro programa (ERROR_SHARING_VIOLATION) → "Otro programa (quizá el antivirus)
    ///     está usando el archivo descargado…";
    ///   - ruta absoluta o fuera del destino (zip-slip) → "El archivo contiene rutas no válidas.".
    /// Cancelación → OperationCanceledException (el llamador borra el destino a medio extraer).
    /// </summary>
    public static Task<IReadOnlyList<ExtractedFile>> ExtractAsync(string archivePath, string destinationDir,
        IProgress<ExtractProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        return Task.Run(() => ExtractCore(archivePath, destinationDir, progress, ct), ct);
    }

    /// <summary>
    /// Devuelve la carpeta que realmente contiene el juego dentro de lo extraído:
    /// si <paramref name="executableName"/> está en la raíz → la raíz; si no, busca en subcarpetas hasta profundidad 3
    /// (en anchura; ignora carpetas "__MACOSX"). Si no aparece, y la raíz tiene exactamente una subcarpeta y ningún
    /// archivo, desciende por ella (máx. 3 niveles) y devuelve esa. Null si no hay nada razonable (carpeta vacía).
    /// </summary>
    public static string? FindContentRoot(string extractedDir, string executableName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(extractedDir) || !Directory.Exists(extractedDir))
            {
                return null;
            }

            var root = FileSystemUtil.NormalizePath(extractedDir);

            if (!string.IsNullOrWhiteSpace(executableName))
            {
                // Búsqueda en anchura: la coincidencia menos profunda gana.
                var queue = new Queue<(string Dir, int Depth)>();
                queue.Enqueue((root, 0));
                while (queue.Count > 0)
                {
                    var (dir, depth) = queue.Dequeue();
                    if (File.Exists(Path.Combine(dir, executableName)))
                    {
                        return dir;
                    }
                    if (depth < MaxContentRootDepth)
                    {
                        foreach (var sub in GetContentSubdirectories(dir))
                        {
                            queue.Enqueue((sub, depth + 1));
                        }
                    }
                }
            }

            // Sin exe: si la raíz es solo "una carpeta dentro de otra", se baja por ella.
            var current = root;
            for (var level = 0; level < MaxContentRootDepth; level++)
            {
                var subs = GetContentSubdirectories(current);
                if (subs.Count == 1 && !HasContentFiles(current))
                {
                    current = subs[0];
                }
                else
                {
                    break;
                }
            }

            return HasContentFiles(current) || GetContentSubdirectories(current).Count > 0 ? current : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo analizar la carpeta extraída {extractedDir}.", ex);
            return null;
        }
    }

    // ───────────────────────── Extracción ─────────────────────────

    private static IReadOnlyList<ExtractedFile> ExtractCore(string archivePath, string destinationDir,
        IProgress<ExtractProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive))
        {
            throw new LauncherException("No se encuentra el archivo descargado. Vuelve a intentarlo.");
        }

        var dest = FileSystemUtil.NormalizePath(destinationDir);
        var context = new ExtractContext(dest, progress, ct);
        PrepareDestination(dest, context);

        ArchiveFormat format;
        try
        {
            format = DetectFormatOrThrow(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // P.ej. el antivirus lo bloquea al abrirlo: no es un archivo dañado.
            throw MapReadError(ex, archive, dest);
        }
        Log.Info($"Extrayendo {Path.GetFileName(archive)} ({format}) en {dest}");
        var stopwatch = Stopwatch.StartNew();

        IReadOnlyList<ExtractedFile> result = format switch
        {
            ArchiveFormat.Zip => ExtractZip(archive, context),
            ArchiveFormat.Rar or ArchiveFormat.SevenZip => ExtractWithSharpCompress(archive, format, context),
            _ => throw UnknownFormat(archive),
        };

        context.ReportNow();
        Log.Info($"Extracción completada: {result.Count} archivos, {FileSystemUtil.FormatBytes(context.BytesDone)} en {stopwatch.Elapsed.TotalSeconds:0.0} s.");
        return result;
    }

    private static LauncherException UnknownFormat(string archive)
    {
        Log.Warn($"Formato de archivo no reconocido (bytes mágicos): {archive}");
        return new LauncherException(CorruptMessage);
    }

    private static void PrepareDestination(string dest, ExtractContext context)
    {
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            throw new InvalidOperationException($"La carpeta de extracción no está vacía: {dest}");
        }
        try
        {
            Directory.CreateDirectory(dest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapWriteError(ex, dest, context);
        }
    }

    private static IReadOnlyList<ExtractedFile> ExtractZip(string archive, ExtractContext context)
    {
        ZipArchive zip;
        IReadOnlyList<ZipArchiveEntry> entries;
        try
        {
            zip = ZipFile.OpenRead(archive);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapReadError(ex, archive, context.Destination);
        }

        using (zip)
        {
            try
            {
                entries = zip.Entries;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw MapReadError(ex, archive, context.Destination);
            }

            // Primero se validan TODAS las rutas: si hay una maliciosa no se escribe nada.
            var plan = new List<(ZipArchiveEntry Entry, string FullPath, bool IsDirectory)>(entries.Count);
            foreach (var entry in entries)
            {
                var isDirectory = IsZipDirectory(entry);
                var full = ResolveEntryPath(context.Destination, entry.FullName);
                if (full is null)
                {
                    continue;
                }
                if (!isDirectory && entry.IsEncrypted)
                {
                    throw new LauncherException(PasswordMessage);
                }
                plan.Add((entry, full, isDirectory));
                if (!isDirectory)
                {
                    context.EntriesTotal++;
                    context.BytesTotal += Math.Max(0, entry.Length);
                }
            }

            context.ReportNow();
            foreach (var (entry, full, isDirectory) in plan)
            {
                context.Ct.ThrowIfCancellationRequested();
                if (isDirectory)
                {
                    CreateDirectory(full, context);
                    continue;
                }

                Stream source;
                try
                {
                    source = entry.Open();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw MapReadError(ex, archive, context.Destination);
                }

                using (source)
                {
                    var lastWrite = entry.LastWriteTime.Year is >= 1980 and <= 2107 ? entry.LastWriteTime.UtcDateTime : (DateTime?)null;
                    WriteEntry(source, full, lastWrite, archive, context);
                }
            }
        }

        return context.Files;
    }

    private static IReadOnlyList<ExtractedFile> ExtractWithSharpCompress(string archive, ArchiveFormat format, ExtractContext context)
    {
        IArchive sharp;
        List<IArchiveEntry> entries;
        try
        {
            sharp = OpenSharpArchive(archive, format);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapReadError(ex, archive, context.Destination);
        }

        using (sharp)
        {
            bool useReader;
            try
            {
                entries = sharp.Entries.ToList();
                if (sharp.IsEncrypted)
                {
                    throw new LauncherException(PasswordMessage);
                }
                // 7z (sólido o no) y RAR sólido: lectura secuencial (cada entrada sin rebobinar). RAR normal: acceso directo.
                useReader = format == ArchiveFormat.SevenZip || sharp.IsSolid;
            }
            catch (LauncherException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw MapReadError(ex, archive, context.Destination);
            }

            // Validación previa de todas las rutas.
            foreach (var entry in entries)
            {
                var full = ResolveEntryPath(context.Destination, entry.Key ?? "");
                if (full is null || entry.IsDirectory || IsLink(entry))
                {
                    continue;
                }
                // SharpCompress marca a veces como cifradas las entradas vacías de 7z: solo cuentan las que tienen datos.
                if (entry.IsEncrypted && entry.Size > 0)
                {
                    throw new LauncherException(PasswordMessage);
                }
                context.EntriesTotal++;
                context.BytesTotal += Math.Max(0, entry.Size);
            }

            context.ReportNow();
            if (useReader)
            {
                SharpCompress.Readers.IReader reader;
                try
                {
                    reader = sharp.ExtractAllEntries();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw MapReadError(ex, archive, context.Destination);
                }

                using (reader)
                {
                    while (true)
                    {
                        context.Ct.ThrowIfCancellationRequested();
                        bool hasNext;
                        try
                        {
                            hasNext = reader.MoveToNextEntry();
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            throw MapReadError(ex, archive, context.Destination);
                        }
                        if (!hasNext)
                        {
                            break;
                        }

                        var entry = reader.Entry;
                        ExtractSharpEntry(entry, () => reader.OpenEntryStream(), archive, context);
                    }
                }
            }
            else
            {
                foreach (var entry in entries)
                {
                    context.Ct.ThrowIfCancellationRequested();
                    ExtractSharpEntry(entry, entry.OpenEntryStream, archive, context);
                }
            }
        }

        return context.Files;
    }

    private static void ExtractSharpEntry(IEntry entry, Func<Stream> openStream, string archive, ExtractContext context)
    {
        var full = ResolveEntryPath(context.Destination, entry.Key ?? "");
        if (full is null)
        {
            return;
        }
        if (entry.IsDirectory)
        {
            CreateDirectory(full, context);
            return;
        }
        if (IsLink(entry))
        {
            Log.Warn($"Se omite el enlace simbólico del archivo comprimido: {entry.Key} → {entry.LinkTarget}");
            return;
        }

        Stream source;
        try
        {
            source = openStream();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw MapReadError(ex, archive, context.Destination);
        }

        using (source)
        {
            DateTime? lastWrite = null;
            if (entry.LastModifiedTime is { } modified && modified.Year is >= 1980 and <= 2107)
            {
                lastWrite = modified.Kind == DateTimeKind.Utc ? modified : modified.ToUniversalTime();
            }
            WriteEntry(source, full, lastWrite, archive, context);
        }
    }

    private static bool IsLink(IEntry entry) => !string.IsNullOrEmpty(entry.LinkTarget);

    private static IArchive OpenSharpArchive(string archivePath, ArchiveFormat format) => format switch
    {
        ArchiveFormat.Rar => RarArchive.OpenArchive(archivePath),
        ArchiveFormat.SevenZip => SevenZipArchive.OpenArchive(archivePath),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static bool IsZipDirectory(ZipArchiveEntry entry)
        => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>
    /// Ruta absoluta de una entrada dentro de <paramref name="destination"/> (ya normalizado, sin separador final).
    /// Null si la entrada es la propia raíz o está vacía. Rutas absolutas, con caracteres inválidos/nombres de dispositivo
    /// o que salgan del destino → LauncherException("El archivo contiene rutas no válidas.").
    /// </summary>
    internal static string? ResolveEntryPath(string destination, string entryName)
    {
        if (string.IsNullOrEmpty(entryName))
        {
            return null;
        }

        var name = entryName.Replace('/', '\\');
        if (Path.IsPathRooted(name) || name.StartsWith('\\'))
        {
            throw InvalidPaths(entryName);
        }

        foreach (var segment in name.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                continue;
            }
            if (segment.IndexOfAny(InvalidSegmentChars) >= 0)
            {
                throw InvalidPaths(entryName);
            }
            var baseName = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedDeviceNames.Contains(baseName))
            {
                throw InvalidPaths(entryName);
            }
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(destination, name)).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(InvalidPathsMessage, ex);
        }

        if (string.Equals(full, destination, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!full.StartsWith(destination + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidPaths(entryName);
        }
        return full;
    }

    private static LauncherException InvalidPaths(string entryName)
    {
        Log.Warn($"Entrada con ruta no válida en el archivo comprimido: \"{entryName}\"");
        return new LauncherException(InvalidPathsMessage);
    }

    private static void CreateDirectory(string path, ExtractContext context)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapWriteError(ex, path, context);
        }
    }

    /// <summary>Copia una entrada a disco por bloques, con Flush(true), fecha y registro en la lista de extraídos.</summary>
    private static void WriteEntry(Stream source, string targetPath, DateTime? lastWriteUtc, string archive, ExtractContext context)
    {
        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            CreateDirectory(parent, context);
        }

        FileStream output;
        try
        {
            output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw MapWriteError(ex, targetPath, context);
        }

        long written = 0;
        using (output)
        {
            var buffer = context.Buffer;
            while (true)
            {
                context.Ct.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = source.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw MapReadError(ex, archive, context.Destination);
                }
                if (read == 0)
                {
                    break;
                }

                try
                {
                    output.Write(buffer, 0, read);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw MapWriteError(ex, targetPath, context);
                }
                written += read;
                context.AddBytes(read);
            }

            try
            {
                output.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw MapWriteError(ex, targetPath, context);
            }

            if (lastWriteUtc is { } time)
            {
                try
                {
                    // Por el handle: tras fijarla explícitamente, cerrar el archivo ya no la cambia.
                    File.SetLastWriteTimeUtc(output.SafeFileHandle, time);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    context.WarnTimestampOnce(targetPath, ex);
                }
            }
        }

        context.AddFile(targetPath, written);
    }

    /// <summary>
    /// Error al leer/descomprimir el archivo → dañado; o antivirus si bloqueó la lectura; "no se encuentra" si desapareció;
    /// "en uso" si otro programa lo tiene abierto en exclusiva.
    /// </summary>
    private static LauncherException MapReadError(Exception ex, string archive, string? destinationDir)
    {
        if (ex is LauncherException launcherException)
        {
            return launcherException;
        }
        if (ex is UnauthorizedAccessException || (ex is IOException && ex.HResult is HResultVirusInfected or HResultVirusDeleted))
        {
            Log.Warn($"El antivirus bloqueó la lectura de {archive}.", ex);
            return new LauncherException(AntivirusMessage(Path.GetFileName(archive), ExclusionFolder(destinationDir ?? Path.GetDirectoryName(archive) ?? archive)), ex);
        }
        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Log.Warn($"El archivo descargado desapareció antes de poder leerlo: {archive}", ex);
            return new LauncherException("No se encuentra el archivo descargado. Vuelve a intentarlo.", ex);
        }
        if (FileSystemUtil.IsSharingViolation(ex))
        {
            Log.Warn($"El archivo descargado está en uso por otro programa: {archive}", ex);
            return new LauncherException(InUseMessage, ex);
        }
        if (ex is not (InvalidDataException or SharpCompressException or EndOfStreamException or IOException))
        {
            // Los decodificadores fallan con excepciones variadas ante datos corruptos: se registran completas.
            Log.Error($"Error inesperado al leer {archive}; se trata como archivo dañado.", ex);
        }
        else
        {
            Log.Warn($"Archivo comprimido dañado o no soportado: {archive}", ex);
        }
        return new LauncherException(CorruptMessage, ex);
    }

    /// <summary>Error al escribir en el destino → disco lleno / antivirus / ruta larga / genérico.</summary>
    private static LauncherException MapWriteError(Exception ex, string targetPath, ExtractContext context)
    {
        var name = Path.GetFileName(targetPath.TrimEnd('\\'));
        if (FileSystemUtil.IsDiskFull(ex))
        {
            Log.Warn($"Disco lleno al extraer {targetPath}.", ex);
            return new LauncherException(
                $"No hay espacio suficiente en el disco ({Path.GetPathRoot(targetPath)}). Libera espacio y vuelve a intentarlo.", ex);
        }
        if (ex is UnauthorizedAccessException || (ex is IOException && ex.HResult is HResultVirusInfected or HResultVirusDeleted))
        {
            Log.Warn($"Escritura bloqueada (¿antivirus?) al extraer {targetPath}.", ex);
            return new LauncherException(AntivirusMessage(name, ExclusionFolder(context.Destination)), ex);
        }
        if (ex is PathTooLongException)
        {
            Log.Warn($"Ruta demasiado larga al extraer {targetPath}.", ex);
            return new LauncherException(
                "La carpeta de instalación tiene una ruta demasiado larga. Elige una carpeta más corta (por ejemplo D:\\Juegos).", ex);
        }
        Log.Warn($"Error de escritura al extraer {targetPath}.", ex);
        return new LauncherException($"No se pudo escribir «{name}» en el disco. Vuelve a intentarlo.", ex);
    }

    private static string AntivirusMessage(string fileName, string folder)
        => $"El antivirus bloqueó un archivo del juego ({fileName}). Añade una exclusión para {folder} y vuelve a intentarlo.";

    /// <summary>
    /// Carpeta a excluir en el antivirus: la InstallRoot si el destino está dentro de su ".launcher" (staging del
    /// instalador); si no, el propio destino.
    /// </summary>
    private static string ExclusionFolder(string destination)
        => FileSystemUtil.GetAntivirusExclusionFolder(destination, isDirectory: true);

    // ───────────────────────── Raíz de contenido ─────────────────────────

    private static List<string> GetContentSubdirectories(string dir)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        return Directory.EnumerateDirectories(dir, "*", options)
            .Where(d => !Path.GetFileName(d).Equals(MacOsJunkFolder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasContentFiles(string dir)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false };
        return Directory.EnumerateFiles(dir, "*", options).Any(f => !JunkFileNames.Contains(Path.GetFileName(f)));
    }

    // ───────────────────────── Contexto ─────────────────────────

    private sealed class ExtractContext
    {
        private readonly IProgress<ExtractProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<ExtractedFile> _files = new();
        private readonly Dictionary<string, int> _fileIndex = new(StringComparer.OrdinalIgnoreCase);
        private TimeSpan _lastReport = -ProgressInterval;
        private bool _timestampWarned;

        public ExtractContext(string destination, IProgress<ExtractProgress>? progress, CancellationToken ct)
        {
            Destination = destination;
            _progress = progress;
            Ct = ct;
        }

        public string Destination { get; }
        public CancellationToken Ct { get; }
        public byte[] Buffer { get; } = new byte[BufferSize];
        public int EntriesTotal { get; set; }
        public long BytesTotal { get; set; }
        public int EntriesDone { get; private set; }
        public long BytesDone { get; private set; }
        public IReadOnlyList<ExtractedFile> Files => _files;

        public void AddBytes(int count)
        {
            BytesDone += count;
            MaybeReport();
        }

        public void AddFile(string fullPath, long length)
        {
            var relative = Path.GetRelativePath(Destination, fullPath);
            var file = new ExtractedFile(relative, length);
            // Entradas duplicadas: la última gana (es la que queda en disco).
            if (_fileIndex.TryGetValue(fullPath, out var index))
            {
                _files[index] = file;
            }
            else
            {
                _fileIndex[fullPath] = _files.Count;
                _files.Add(file);
            }
            EntriesDone++;
            MaybeReport();
        }

        public void ReportNow()
        {
            if (_progress is null)
            {
                return;
            }
            _lastReport = _clock.Elapsed;
            _progress.Report(new ExtractProgress(EntriesDone, Math.Max(EntriesTotal, EntriesDone), BytesDone, Math.Max(BytesTotal, BytesDone)));
        }

        public void WarnTimestampOnce(string path, Exception ex)
        {
            if (_timestampWarned)
            {
                return;
            }
            _timestampWarned = true;
            Log.Warn($"No se pudo fijar la fecha de modificación de {path} (se ignoran los siguientes).", ex);
        }

        private void MaybeReport()
        {
            if (_progress is not null && _clock.Elapsed - _lastReport >= ProgressInterval)
            {
                ReportNow();
            }
        }
    }
}
