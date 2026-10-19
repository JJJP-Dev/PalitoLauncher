using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Settings;

/// <summary>
/// Carga/guarda <see cref="LauncherSettings"/> en JSON indentado (System.Text.Json, JsonStringEnumConverter,
/// propiedades desconocidas ignoradas). Thread-safe (lock interno).
/// </summary>
public sealed class SettingsStore
{
    /// <summary>Copias de archivos corruptos que se conservan.</summary>
    internal const int MaxCorruptCopies = 3;

    private const int ReadAttempts = 3;
    private const int ReadRetryDelayMs = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Propiedades persistidas (públicas con get/set). Se leen una a una para que un valor dañado no tire todo el archivo.
    private static readonly PropertyInfo[] PersistedProperties = typeof(LauncherSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0
            && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
        .ToArray();

    private readonly object _lock = new();
    private readonly string _filePath;

    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    private string BackupPath => _filePath + ".bak";

    /// <summary>
    /// Nunca lanza. Si el JSON se lee bien, copia settings.json → settings.json.bak (best effort).
    /// Si settings.json no existe, está vacío (corte de luz) o corrupto → intenta settings.json.bak; si tampoco → valores
    /// por defecto. Un archivo corrupto se renombra a "settings.corrupt-yyyyMMddHHmmss.json" (se conservan solo los 3 más
    /// recientes) y se registra un Warn. Sanea valores: MaxDownloadSpeedKBps &lt; 0 → 0; LaunchArguments null → "";
    /// enum inválido → por defecto.
    /// </summary>
    public LauncherSettings Load()
    {
        try
        {
            lock (_lock)
                return LoadCore();
        }
        catch (Exception ex)
        {
            Log.Error($"Error inesperado al cargar los ajustes de {_filePath}; se usan los valores por defecto.", ex);
            return new LauncherSettings();
        }
    }

    /// <summary>
    /// Guarda de forma atómica (FileSystemUtil.WriteAllTextAtomic, que ya reintenta). Devuelve false (y registra) si falla.
    /// Nunca lanza. Serializa sobre una copia (<see cref="LauncherSettings.Clone"/>) tomada bajo el lock.
    /// </summary>
    public bool Save(LauncherSettings settings)
    {
        if (settings == null)
        {
            Log.Warn("SettingsStore.Save llamado con null; no se guarda nada.");
            return false;
        }

        try
        {
            lock (_lock)
            {
                var copy = settings.Clone();
                copy.LaunchArguments ??= "";
                var json = JsonSerializer.Serialize(copy, JsonOptions);
                FileSystemUtil.WriteAllTextAtomic(_filePath, json);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudieron guardar los ajustes en {_filePath}.", ex);
            return false;
        }
    }

    private LauncherSettings LoadCore()
    {
        var mainText = TryReadText(_filePath, out var mainState, out var mainError);
        switch (mainState)
        {
            case ReadState.Ok:
                if (TryParse(mainText!, out var settings, out var parseError))
                {
                    WriteBackup(mainText!);
                    return settings;
                }
                Log.Warn($"Los ajustes ({Path.GetFileName(_filePath)}) están dañados: {parseError}");
                QuarantineCorrupt();
                break;
            case ReadState.Empty:
                Log.Warn($"Los ajustes ({Path.GetFileName(_filePath)}) están vacíos (¿corte de luz?).");
                break;
            case ReadState.Unreadable:
                Log.Warn($"No se pudieron leer los ajustes ({_filePath}).", mainError);
                break;
            case ReadState.Missing:
                break;
        }

        var backupText = TryReadText(BackupPath, out var backupState, out var backupError);
        if (backupState == ReadState.Ok)
        {
            if (TryParse(backupText!, out var fromBackup, out var backupParseError))
            {
                Log.Warn("Se usan los ajustes de la copia de seguridad (settings.json.bak).");
                return fromBackup;
            }
            Log.Warn($"La copia de seguridad de los ajustes también está dañada: {backupParseError}");
        }
        else if (backupState == ReadState.Unreadable)
        {
            Log.Warn($"No se pudo leer la copia de seguridad de los ajustes ({BackupPath}).", backupError);
        }

        if (mainState == ReadState.Missing && backupState == ReadState.Missing)
            Log.Info("No hay ajustes guardados todavía: se usan los valores por defecto.");
        else
            Log.Warn("Se usan los ajustes por defecto.");
        return new LauncherSettings();
    }

    private enum ReadState { Ok, Missing, Empty, Unreadable }

    // Lee el archivo compartiendo lectura/escritura (antivirus, otra instancia); reintenta ante bloqueos breves.
    private static string? TryReadText(string path, out ReadState state, out Exception? error)
    {
        error = null;
        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    state = ReadState.Missing;
                    return null;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                state = string.IsNullOrWhiteSpace(text) || text.Trim('\0', ' ', '\r', '\n', '\t').Length == 0
                    ? ReadState.Empty
                    : ReadState.Ok;
                return text;
            }
            catch (FileNotFoundException)
            {
                state = ReadState.Missing;
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                state = ReadState.Missing;
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex;
                if (attempt < ReadAttempts)
                    Thread.Sleep(ReadRetryDelayMs);
            }
        }

        state = ReadState.Unreadable;
        return null;
    }

    /// <summary>
    /// Parsea los ajustes propiedad a propiedad: un JSON ilegible (o que no es un objeto) es "corrupto"; un valor concreto
    /// inválido (tipo incorrecto, enum desconocido) solo se ignora y conserva su valor por defecto.
    /// </summary>
    internal static bool TryParse(string json, out LauncherSettings settings, out string? error)
    {
        settings = new LauncherSettings();
        error = null;
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"se esperaba un objeto JSON y hay {root.ValueKind}";
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                var target = Array.Find(PersistedProperties,
                    p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    continue; // propiedad desconocida (versión más nueva/vieja del launcher)

                if (TryConvert(property.Value, target.PropertyType, out var value))
                    target.SetValue(settings, value);
                else
                    Log.Warn($"Ajuste '{target.Name}' con valor no válido ({Truncate(property.Value.GetRawText())}); se usa el valor por defecto.");
            }

            Sanitize(settings);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            settings = new LauncherSettings();
            return false;
        }
    }

    private static bool TryConvert(JsonElement element, Type type, out object? value)
    {
        value = null;
        var underlying = Nullable.GetUnderlyingType(type);
        var isNullableValueType = underlying != null;
        var effective = underlying ?? type;

        if (element.ValueKind == JsonValueKind.Null)
        {
            // null solo vale para referencias o Nullable<T>.
            return !effective.IsValueType || isNullableValueType;
        }

        if (effective.IsEnum)
            return TryConvertEnum(element, effective, out value);

        try
        {
            value = element.Deserialize(type, JsonOptions);
            return value != null || !effective.IsValueType || isNullableValueType;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
        {
            return false;
        }
    }

    private static bool TryConvertEnum(JsonElement element, Type enumType, out object? value)
    {
        value = null;
        object? candidate = null;
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse(enumType, text.Trim(), ignoreCase: true, out var parsed))
                candidate = parsed;
        }
        else if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            candidate = Enum.ToObject(enumType, number);
        }

        if (candidate == null || !Enum.IsDefined(enumType, candidate))
            return false;
        value = candidate;
        return true;
    }

    private static void Sanitize(LauncherSettings settings)
    {
        var defaults = new LauncherSettings();

        if (settings.MaxDownloadSpeedKBps < 0)
            settings.MaxDownloadSpeedKBps = 0;
        settings.LaunchArguments ??= "";
        if (settings.SchemaVersion < 1)
            settings.SchemaVersion = defaults.SchemaVersion;

        foreach (var property in PersistedProperties)
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (!type.IsEnum)
                continue;
            var current = property.GetValue(settings);
            if (current != null && !Enum.IsDefined(type, current))
                property.SetValue(settings, property.GetValue(defaults));
        }

        // Una ruta relativa o vacía como carpeta de instalación sería peligrosa: se olvida (el usuario la volverá a elegir).
        if (settings.InstallRoot != null)
        {
            var root = settings.InstallRoot.Trim();
            bool valid;
            try { valid = root.Length > 0 && Path.IsPathFullyQualified(root) && root.IndexOf('\0') < 0; }
            catch (ArgumentException) { valid = false; }
            if (!valid)
            {
                Log.Warn($"InstallRoot no válida en los ajustes ('{settings.InstallRoot}'); se ignora.");
                settings.InstallRoot = null;
            }
            else
            {
                settings.InstallRoot = root;
            }
        }
    }

    // Copia de seguridad del último archivo bueno (escritura atómica para que el .bak nunca quede a medias).
    private void WriteBackup(string text)
    {
        try
        {
            FileSystemUtil.WriteAllTextAtomic(BackupPath, text);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo actualizar la copia de seguridad de los ajustes ({BackupPath}).", ex);
        }
    }

    // Renombra el archivo corrupto a "settings.corrupt-yyyyMMddHHmmss.json" y conserva solo los más recientes.
    private void QuarantineCorrupt()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_filePath)) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(_filePath);
            var extension = Path.GetExtension(_filePath);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            var destination = Path.Combine(dir, $"{baseName}.corrupt-{stamp}{extension}");
            for (var i = 2; File.Exists(destination) && i < 1000; i++)
                destination = Path.Combine(dir, $"{baseName}.corrupt-{stamp}-{i.ToString(CultureInfo.InvariantCulture)}{extension}");

            File.Move(_filePath, destination);
            Log.Warn($"El archivo de ajustes dañado se guardó como {Path.GetFileName(destination)}.");
            PruneCorruptCopies(dir, baseName, extension);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo apartar el archivo de ajustes dañado ({_filePath}).", ex);
        }
    }

    private static void PruneCorruptCopies(string dir, string baseName, string extension)
    {
        var prefix = baseName + ".corrupt-";
        var copies = Directory.EnumerateFiles(dir, prefix + "*" + extension)
            .Select(path => (Path: path, Key: CorruptSortKey(Path.GetFileName(path), prefix, extension)))
            .OrderByDescending(x => x.Key, StringComparer.Ordinal)
            .Skip(MaxCorruptCopies)
            .ToList();

        foreach (var (path, _) in copies)
        {
            try { File.Delete(path); }
            catch (Exception ex) { Log.Warn($"No se pudo borrar la copia de ajustes dañada {path}.", ex); }
        }
    }

    // "settings.corrupt-20261019134501-2.json" → "20261019134501-0002" (ordenable de forma ordinal).
    internal static string CorruptSortKey(string fileName, string prefix, string extension)
    {
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || fileName.Length < prefix.Length + extension.Length)
            return "";

        var middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - extension.Length);
        var parts = middle.Split('-', 2);
        if (parts[0].Length != 14 || !parts[0].All(char.IsAsciiDigit))
            return "";
        var suffix = parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return parts[0] + "-" + suffix.ToString("D4", CultureInfo.InvariantCulture);
    }

    private static string Truncate(string text) => text.Length <= 60 ? text : text[..60] + "…";
}
