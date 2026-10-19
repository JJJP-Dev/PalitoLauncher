using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using PalitoLauncher.Core.Install;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Update;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Diagnostics;

/// <summary>Datos que la UI pasa para el resumen del informe.</summary>
public sealed class DiagnosticContext
{
    /// <summary>Pares clave/valor para summary.txt (estado, versión instalada/última, origen de los datos, etc.).</summary>
    public IReadOnlyDictionary<string, string?> Summary { get; init; } = new Dictionary<string, string?>();

    public string? InstallRoot { get; init; }

    /// <summary>Carpeta de datos de Unity (GameInstaller.GetUnityPersistentDataDir), si se conoce.</summary>
    public string? UnityDataDir { get; init; }
}

/// <summary>
/// Genera un .zip para enviar al desarrollador cuando algo falla, sin que el usuario tenga que buscar carpetas ocultas.
/// </summary>
public static class DiagnosticReport
{
    /// <summary>Tamaño máximo de cada archivo incluido.</summary>
    internal const long MaxFileBytes = 20L * 1024 * 1024;

    private static readonly TimeSpan LogMaxAge = TimeSpan.FromDays(7);

    // Máximo de entradas listadas por carpeta en el resumen (para no generar un summary enorme).
    private const int MaxListedEntries = 60;

    /// <summary>
    /// Línea(s) de resumen del entorno: versión del launcher, Windows (Environment.OSVersion + RuntimeInformation),
    /// ProcessPath, .NET, cultura. Se usa también como cabecera del log al arrancar.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> GetEnvironmentSummary()
    {
        var result = new Dictionary<string, string?>();
        Add(result, "Launcher", () => LauncherSelfUpdater.CurrentVersionText);
        Add(result, "Windows", () => $"{Environment.OSVersion.VersionString} · {RuntimeInformation.OSDescription}");
        Add(result, "Arquitectura", () => $"SO {RuntimeInformation.OSArchitecture}, proceso {RuntimeInformation.ProcessArchitecture}");
        Add(result, ".NET", () => RuntimeInformation.FrameworkDescription);
        Add(result, "Ejecutable", () => Environment.ProcessPath ?? "(desconocido)");
        Add(result, "Cultura", () => $"{CultureInfo.CurrentCulture.Name} (UI {CultureInfo.CurrentUICulture.Name})");
        Add(result, "Zona horaria", () => TimeZoneInfo.Local.Id);
        Add(result, "Procesadores", () => Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        return result;

        static void Add(Dictionary<string, string?> dict, string key, Func<string?> getter)
        {
            try
            {
                dict[key] = getter();
            }
            catch (Exception ex)
            {
                dict[key] = $"(error: {ex.GetType().Name})";
            }
        }
    }

    /// <summary>
    /// Crea "&lt;outputDir&gt;\Coop2D-informe-yyyyMMdd-HHmm.zip" (System.IO.Compression) con:
    /// summary.txt (entorno + context.Summary + espacio libre de la unidad de InstallRoot), los logs del launcher
    /// (AppPaths.LogsDir, últimos 7 días), settings.json, game\palito_install.json, Player.log y Player-prev.log de
    /// UnityDataDir, y la carpeta más reciente de %TEMP%\&lt;compañía&gt;\&lt;producto&gt;\Crashes si existe (límite 20 MB por
    /// archivo). Abre cada archivo con FileShare.ReadWrite | FileShare.Delete; los que fallen se anotan en summary.txt.
    /// Corre en segundo plano. Devuelve la ruta del zip. LauncherException si no se puede crear.
    /// </summary>
    public static Task<string> CreateAsync(DiagnosticContext context, string outputDir, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.Run(() => Create(context, outputDir, AppPaths.LogsDir, AppPaths.SettingsFile, Path.GetTempPath(), ct), ct);
    }

    /// <summary>Implementación síncrona con rutas inyectables (tests).</summary>
    internal static string Create(DiagnosticContext context, string outputDir, string logsDir, string settingsFile,
        string tempDir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new LauncherException("No se pudo crear el informe: carpeta de destino no válida.");

        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo crear la carpeta del informe: {outputDir}", ex);
            throw new LauncherException($"No se pudo crear el informe en {outputDir}.", ex);
        }

        var zipPath = GetUniqueZipPath(outputDir, DateTime.Now);
        var tmpPath = zipPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var included = new List<string>();
        var problems = new List<string>();

        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                AddLogs(zip, logsDir, included, problems, ct);
                AddFile(zip, settingsFile, "settings.json", included, problems, ct, optional: true);

                if (!string.IsNullOrWhiteSpace(context.InstallRoot))
                {
                    var marker = Path.Combine(GameInstaller.GetGameDir(context.InstallRoot), InstalledGameInfo.MarkerFileName);
                    AddFile(zip, marker, "game/" + InstalledGameInfo.MarkerFileName, included, problems, ct, optional: true);
                }

                if (!string.IsNullOrWhiteSpace(context.UnityDataDir))
                {
                    AddFile(zip, Path.Combine(context.UnityDataDir, "Player.log"), "unity/Player.log", included, problems, ct, optional: true);
                    AddFile(zip, Path.Combine(context.UnityDataDir, "Player-prev.log"), "unity/Player-prev.log", included, problems, ct,
                        optional: true);
                    AddLatestCrash(zip, context.UnityDataDir, tempDir, included, problems, ct);
                }

                ct.ThrowIfCancellationRequested();
                var summary = BuildSummary(context, included, problems);
                var entry = zip.CreateEntry("summary.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
                {
                    writer.Write(summary);
                }
            }

            File.Move(tmpPath, zipPath, overwrite: false);
            Log.Info($"Informe creado: {zipPath} ({included.Count} archivos, {problems.Count} avisos).");
            return zipPath;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            Log.Error($"No se pudo crear el informe en {outputDir}.", ex);
            if (ex is LauncherException)
                throw;
            throw new LauncherException($"No se pudo crear el informe en {outputDir}. Comprueba que hay espacio libre.", ex);
        }
    }

    // ---------------------------------------------------------------- partes del informe

    private static void AddLogs(ZipArchive zip, string logsDir, List<string> included, List<string> problems, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(logsDir))
            {
                problems.Add($"Carpeta de logs no encontrada: {logsDir}");
                return;
            }

            var cutoff = DateTime.UtcNow - LogMaxAge;
            var files = new DirectoryInfo(logsDir).EnumerateFiles("*.log")
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
                problems.Add("No hay logs del launcher de los últimos 7 días.");

            foreach (var f in files)
                AddFile(zip, f.FullName, "logs/" + f.Name, included, problems, ct, optional: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            problems.Add($"No se pudo leer la carpeta de logs ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Carpeta más reciente de %TEMP%\&lt;compañía&gt;\&lt;producto&gt;\Crashes (Unity).</summary>
    private static void AddLatestCrash(ZipArchive zip, string unityDataDir, string tempDir, List<string> included, List<string> problems,
        CancellationToken ct)
    {
        try
        {
            var trimmed = unityDataDir.TrimEnd('\\', '/');
            var product = Path.GetFileName(trimmed);
            var company = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
            if (string.IsNullOrEmpty(product) || string.IsNullOrEmpty(company))
                return;

            var crashesDir = Path.Combine(tempDir, company, product, "Crashes");
            if (!Directory.Exists(crashesDir))
                return;

            var latest = new DirectoryInfo(crashesDir).EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest == null)
                return;

            var count = 0;
            foreach (var file in latest.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 50)
                {
                    problems.Add($"Crash {latest.Name}: demasiados archivos, se incluyeron solo los primeros 50.");
                    break;
                }

                var relative = Path.GetRelativePath(latest.FullName, file.FullName).Replace('\\', '/');
                AddFile(zip, file.FullName, $"crashes/{latest.Name}/{relative}", included, problems, ct, optional: false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            problems.Add($"No se pudieron leer los crashes de Unity ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>
    /// Copia un archivo al zip abriéndolo con FileShare.ReadWrite | FileShare.Delete. Los que pasan de 20 MB se omiten (o,
    /// si son logs de texto, se incluyen sus últimos 20 MB). Los fallos se anotan en <paramref name="problems"/>.
    /// </summary>
    private static void AddFile(ZipArchive zip, string sourcePath, string entryName, List<string> included, List<string> problems,
        CancellationToken ct, bool optional)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(sourcePath))
            {
                if (!optional)
                    problems.Add($"{entryName}: no existe ({sourcePath})");
                else
                    problems.Add($"{entryName}: no encontrado");
                return;
            }

            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                81920, FileOptions.SequentialScan);
            var length = input.Length;
            var note = "";
            if (length > MaxFileBytes)
            {
                if (!IsTextLog(sourcePath))
                {
                    problems.Add($"{entryName}: omitido por tamaño ({FormatSize(length)})");
                    return;
                }

                input.Seek(length - MaxFileBytes, SeekOrigin.Begin);
                note = $" (recortado: últimos {FormatSize(MaxFileBytes)} de {FormatSize(length)})";
            }

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            try
            {
                entry.LastWriteTime = File.GetLastWriteTime(sourcePath);
            }
            catch (Exception)
            {
                // Fecha fuera de rango para ZIP: se deja la actual.
            }

            using (var output = entry.Open())
            {
                var buffer = new byte[81920];
                long remaining = Math.Min(length, MaxFileBytes);
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0)
                        break;
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }

            included.Add(entryName + note);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Un archivo que falla no impide el informe: se anota y se sigue.
            problems.Add($"{entryName}: no se pudo leer ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string BuildSummary(DiagnosticContext context, List<string> included, List<string> problems)
    {
        var sb = new StringBuilder();
        var now = DateTimeOffset.Now;
        sb.AppendLine($"Informe de {LauncherConfig.LauncherName} para {LauncherConfig.GameDisplayName}");
        sb.AppendLine($"Creado: {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} " +
                      $"(UTC {now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)})");
        sb.AppendLine();

        sb.AppendLine("== Entorno ==");
        foreach (var (key, value) in GetEnvironmentSummary())
            sb.AppendLine($"{key}: {value}");
        sb.AppendLine();

        sb.AppendLine("== Estado del launcher ==");
        try
        {
            if (context.Summary.Count == 0)
                sb.AppendLine("(sin datos)");
            foreach (var (key, value) in context.Summary)
                sb.AppendLine($"{key}: {value ?? "(null)"}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(error leyendo el estado: {ex.GetType().Name})");
        }

        sb.AppendLine();

        sb.AppendLine("== Instalación ==");
        AppendInstallInfo(sb, context);
        sb.AppendLine();

        sb.AppendLine("== Archivos incluidos ==");
        if (included.Count == 0)
            sb.AppendLine("(ninguno)");
        foreach (var name in included)
            sb.AppendLine("- " + name);
        sb.AppendLine();

        sb.AppendLine("== Archivos no incluidos / avisos ==");
        if (problems.Count == 0)
            sb.AppendLine("(ninguno)");
        foreach (var p in problems)
            sb.AppendLine("- " + p);

        return sb.ToString();
    }

    private static void AppendInstallInfo(StringBuilder sb, DiagnosticContext context)
    {
        var root = context.InstallRoot;
        sb.AppendLine($"InstallRoot: {root ?? "(no elegida)"}");
        sb.AppendLine($"Datos de Unity: {context.UnityDataDir ?? "(desconocido)"}");
        if (string.IsNullOrWhiteSpace(root))
            return;

        try
        {
            var ready = FileSystemUtil.IsDriveReady(root);
            sb.AppendLine($"Unidad lista: {(ready ? "sí" : "no")}");
            if (ready)
            {
                var free = FileSystemUtil.GetAvailableFreeSpace(root);
                sb.AppendLine($"Espacio libre: {(free == long.MaxValue ? "desconocido" : FormatSize(free))}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Espacio libre: (error: {ex.GetType().Name})");
        }

        AppendListing(sb, root, "Contenido de InstallRoot");
        AppendListing(sb, GameInstaller.GetWorkDir(root), "Contenido de .launcher");
        AppendListing(sb, Path.Combine(GameInstaller.GetWorkDir(root), "downloads"), "Contenido de .launcher\\downloads");
    }

    private static void AppendListing(StringBuilder sb, string dir, string title)
    {
        try
        {
            if (!Directory.Exists(dir))
                return;

            sb.AppendLine($"{title}:");
            var count = 0;
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (++count > MaxListedEntries)
                {
                    sb.AppendLine("  …");
                    break;
                }

                var stamp = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                sb.AppendLine(info is FileInfo f
                    ? $"  {info.Name}  ({FormatSize(f.Length)}, {stamp})"
                    : $"  {info.Name}\\  ({stamp})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (no se pudo listar: {ex.GetType().Name})");
        }
    }

    // ---------------------------------------------------------------- utilidades

    internal static string GetUniqueZipPath(string outputDir, DateTime localNow)
    {
        var baseName = $"{LauncherConfig.GameDisplayName}-informe-{localNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(outputDir, baseName + ".zip");
        for (var i = 2; File.Exists(path) && i < 1000; i++)
            path = Path.Combine(outputDir, $"{baseName}-{i}.zip");
        return path;
    }

    private static bool IsTextLog(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes)
    {
        try
        {
            return FileSystemUtil.FormatBytes(bytes);
        }
        catch (Exception)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo borrar {path}: {ex.Message}");
        }
    }
}
