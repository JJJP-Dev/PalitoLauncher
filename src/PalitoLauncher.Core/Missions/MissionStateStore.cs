using System.Text.Json;
using System.Text.Json.Serialization;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Missions;

/// <summary>
/// Guarda el estado de cada conjunto de misiones en &lt;dir&gt;\&lt;id&gt;.json (escritura atómica, como settings.json). Un
/// archivo dañado se aparta (.bad) y se empieza de cero. Se conservan los <see cref="MaxStoredSets"/> más recientes.
/// </summary>
public sealed class MissionStateStore
{
    internal const int MaxStoredSets = 10;
    private const long MaxFileBytes = 1024 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _dir;

    public MissionStateStore(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        _dir = Path.GetFullPath(dir);
    }

    internal string PathFor(string setId) => Path.Combine(_dir, setId + ".json");

    /// <summary>Estado guardado del conjunto, o uno nuevo (CreatedUtc = <paramref name="nowUtc"/>) si no hay o está dañado. Nunca lanza.</summary>
    public MissionSetState LoadOrCreate(string setId, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        var path = PathFor(setId);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= MaxFileBytes)
            {
                var text = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<MissionSetState>(text, JsonOptions);
                if (state is not null && string.Equals(state.SetId, setId, StringComparison.OrdinalIgnoreCase))
                {
                    Normalize(state);
                    return state;
                }
                Log.Warn($"Misiones: el estado de «{setId}» no corresponde a ese conjunto; se empieza de cero.");
                SetAside(path);
            }
            else if (info.Exists)
            {
                Log.Warn($"Misiones: el estado de «{setId}» es demasiado grande; se empieza de cero.");
                SetAside(path);
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"Misiones: el estado de «{setId}» está dañado; se aparta y se empieza de cero.", ex);
            SetAside(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Misiones: no se pudo leer el estado de «{setId}»; se empieza de cero.", ex);
        }

        return new MissionSetState { SetId = setId, CreatedUtc = nowUtc };
    }

    /// <summary>Guarda de forma atómica. False (y registra) si falla.</summary>
    public bool Save(MissionSetState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string json;
        try
        {
            json = Serialize(state);
        }
        catch (Exception ex)
        {
            Log.Warn($"Misiones: no se pudo preparar el estado de «{state.SetId}» para guardarlo.", ex);
            return false;
        }
        return SaveSerialized(state.SetId, json);
    }

    /// <summary>JSON del estado (para serializar en el hilo que lo modifica y escribir en otro).</summary>
    public static string Serialize(MissionSetState state) => JsonSerializer.Serialize(state, JsonOptions);

    /// <summary>Escribe un estado ya serializado con <see cref="Serialize"/>. False (y registra) si falla.</summary>
    public bool SaveSerialized(string setId, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            Directory.CreateDirectory(_dir);
            FileSystemUtil.WriteAllTextAtomic(PathFor(setId), json);
            Prune(setId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Misiones: no se pudo guardar el estado de «{setId}».", ex);
            return false;
        }
    }

    private void Prune(string keepSetId)
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.json")
                .Where(f => !string.Equals(Path.GetFileNameWithoutExtension(f.Name), keepSetId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxStoredSets - 1);
            foreach (var file in files) FileSystemUtil.TryDeleteFile(file.FullName);
        }
        catch (Exception ex)
        {
            Log.Debug($"Misiones: no se pudieron borrar estados antiguos: {ex.Message}");
        }
    }

    private static void Normalize(MissionSetState state)
    {
        state.Missions ??= new Dictionary<string, MissionProgress>(StringComparer.OrdinalIgnoreCase);
        if (!ReferenceEquals(state.Missions.Comparer, StringComparer.OrdinalIgnoreCase))
            state.Missions = new Dictionary<string, MissionProgress>(state.Missions, StringComparer.OrdinalIgnoreCase);
        foreach (var key in state.Missions.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            state.Missions.Remove(key);
        foreach (var progress in state.Missions.Values)
        {
            progress.Parts ??= new List<MissionPartProgress>();
            progress.Parts.RemoveAll(p => p is null);
            if (progress.Note is { Length: > MissionNotes.MaxNoteLength } note) progress.Note = note[..MissionNotes.MaxNoteLength];
        }
        state.ProcessedSessions ??= new List<string>();
        state.ProcessedSessions.RemoveAll(string.IsNullOrEmpty);
        if (string.IsNullOrWhiteSpace(state.ReporterId)) state.ReporterId = Guid.NewGuid().ToString("N")[..6];
    }

    private static void SetAside(string path)
    {
        try
        {
            var bad = path + ".bad";
            FileSystemUtil.TryDeleteFile(bad);
            File.Move(path, bad);
        }
        catch (Exception ex)
        {
            Log.Debug($"Misiones: no se pudo apartar {path}: {ex.Message}");
            FileSystemUtil.TryDeleteFile(path);
        }
    }
}

/// <summary>Límites de lo que escribe el jugador.</summary>
public static class MissionNotes
{
    public const int MaxNoteLength = 300;
}
