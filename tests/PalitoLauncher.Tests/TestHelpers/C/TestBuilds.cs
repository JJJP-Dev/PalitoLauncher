using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PalitoLauncher.Core.GitHub;
using PalitoLauncher.Core.Install;

namespace PalitoLauncher.Tests.TestHelpers.C;

/// <summary>Builds de juego falsas (zip en memoria con la estructura real de Unity), releases y carpetas de juego.</summary>
internal static class TestBuilds
{
    public const string Exe = "Coop2D.exe";

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] CreateZip(IEnumerable<(string Path, byte[] Content)> entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(content, 0, content.Length);
            }
        }
        return memory.ToArray();
    }

    /// <summary>Zip con una carpeta raíz (como los de GitHub): &lt;root&gt;/Coop2D.exe, Coop2D_Data/app.info, …</summary>
    public static byte[] CreateGameZip(string rootFolder, string buildId, params string[] extraFiles)
    {
        var random = new Random(buildId.GetHashCode(StringComparison.Ordinal));
        var level = new byte[48 * 1024];
        random.NextBytes(level);
        var entries = new List<(string, byte[])>
        {
            ($"{rootFolder}/{Exe}", Bytes("MZ fake exe " + buildId)),
            ($"{rootFolder}/UnityPlayer.dll", Bytes("player " + buildId)),
            ($"{rootFolder}/UnityCrashHandler64.exe", Bytes("crash handler")),
            ($"{rootFolder}/Coop2D_Data/app.info", Bytes("PalitoGames\nCoop2D")),
            ($"{rootFolder}/Coop2D_Data/level0", level),
            ($"{rootFolder}/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", Bytes("mono " + buildId)),
        };
        entries.AddRange(extraFiles.Select(f => ($"{rootFolder}/{f}", Bytes(f + " " + buildId))));
        return CreateZip(entries);
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static GameRelease MakeRelease(FakeBuildServer server, long releaseId, long assetId, string tag, string assetName,
        byte[] archive, bool withSha = true, long? declaredSize = null)
    {
        var url = $"https://github.com/JJJP-Dev/Coop2D/releases/download/{tag}/{assetName}";
        server.Add(url, archive);
        var asset = new ReleaseAsset
        {
            Id = assetId,
            Name = assetName,
            Size = declaredSize ?? archive.Length,
            DownloadUrl = url,
            ContentType = "application/zip",
            Sha256 = withSha ? Sha256Hex(archive) : null,
            UpdatedAt = new DateTimeOffset(2026, 10, 18, 22, 58, 0, TimeSpan.Zero),
        };
        var release = new ReleaseInfo
        {
            Id = releaseId,
            TagName = tag,
            Name = tag,
            PublishedAt = new DateTimeOffset(2026, 10, 18, 22, 0, 0, TimeSpan.Zero),
            HtmlUrl = $"https://github.com/JJJP-Dev/Coop2D/releases/tag/{tag}",
            Assets = [asset],
        };
        // Versión null a propósito: estos tests no dependen del parser de versiones.
        return new GameRelease(release, asset, version: null);
    }

    /// <summary>Crea una carpeta de juego a mano (para Recover/GetStatus).</summary>
    public static void CreateGameDir(string dir, string tag, bool withMarker = true, bool withExe = true, long assetId = 1,
        string? sha256 = null)
    {
        Directory.CreateDirectory(Path.Combine(dir, "Coop2D_Data"));
        File.WriteAllText(Path.Combine(dir, "Coop2D_Data", "app.info"), "PalitoGames\nCoop2D");
        File.WriteAllText(Path.Combine(dir, "UnityPlayer.dll"), "player " + tag);
        if (withExe)
            File.WriteAllText(Path.Combine(dir, Exe), "MZ " + tag);
        if (withMarker)
        {
            WriteMarker(dir, new InstalledGameInfo
            {
                Tag = tag,
                ReleaseId = assetId,
                AssetId = assetId,
                AssetName = "2D_" + tag + ".zip",
                AssetSize = 123,
                Sha256 = sha256,
                VersionDisplay = tag,
                ExecutableName = Exe,
                InstalledAtUtc = DateTimeOffset.UtcNow,
            });
        }
    }

    public static void WriteMarker(string gameDir, InstalledGameInfo info) =>
        File.WriteAllText(Path.Combine(gameDir, InstalledGameInfo.MarkerFileName), JsonSerializer.Serialize(info));

    public static InstalledGameInfo? ReadMarker(string gameDir)
    {
        var path = Path.Combine(gameDir, InstalledGameInfo.MarkerFileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<InstalledGameInfo>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;
    }

    public static void WriteJournal(string workDir, string? oldDir, string? newDir, string tag)
    {
        Directory.CreateDirectory(workDir);
        var json = JsonSerializer.Serialize(new { oldDir, newDir, tag, startedUtc = DateTimeOffset.UtcNow });
        File.WriteAllText(Path.Combine(workDir, "swap.json"), json);
    }

    /// <summary>Letra de unidad que no existe en este equipo (null si todas existen).</summary>
    public static string? FindMissingDriveRoot()
    {
        var existing = DriveInfo.GetDrives().Select(d => d.Name.ToUpperInvariant()).ToHashSet();
        foreach (var letter in "QRSTUVWXYZJKLMNOP")
        {
            var root = letter + @":\";
            if (!existing.Contains(root) && !Directory.Exists(root))
                return root;
        }
        return null;
    }
}

/// <summary>IProgress síncrono que guarda todos los avisos (y permite reaccionar a ellos).</summary>
internal sealed class RecordingProgress<T> : IProgress<T>
{
    public ConcurrentQueue<T> Items { get; } = new();
    public Action<T>? OnReport { get; set; }

    public void Report(T value)
    {
        Items.Enqueue(value);
        OnReport?.Invoke(value);
    }
}
