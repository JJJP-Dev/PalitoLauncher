namespace PalitoLauncher.Tests.TestHelpers.B;

/// <summary>Carpeta temporal única para un test; se borra (best effort) al hacer Dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "palito-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <summary>Crea un archivo (y sus carpetas) con el contenido dado y devuelve su ruta completa.</summary>
    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string WriteFile(string relativePath, string content) => WriteFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(Path))
                {
                    return;
                }
                foreach (var entry in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             AttributesToSkip = 0,
                             IgnoreInaccessible = true,
                         }))
                {
                    entry.Attributes = FileAttributes.Normal;
                }
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (Exception)
            {
                // Se deja para la limpieza del sistema.
            }
        }
    }
}

/// <summary>IProgress síncrono (sin SynchronizationContext) que guarda todos los reportes.</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly object _lock = new();
    private readonly List<(T Value, DateTime At)> _items = new();
    private readonly Action<T>? _onReport;

    public SyncProgress(Action<T>? onReport = null) => _onReport = onReport;

    public IReadOnlyList<T> Values
    {
        get
        {
            lock (_lock)
            {
                return _items.Select(i => i.Value).ToList();
            }
        }
    }

    public IReadOnlyList<(T Value, DateTime At)> Timeline
    {
        get
        {
            lock (_lock)
            {
                return _items.ToList();
            }
        }
    }

    public void Report(T value)
    {
        lock (_lock)
        {
            _items.Add((value, DateTime.UtcNow));
        }
        _onReport?.Invoke(value);
    }
}
