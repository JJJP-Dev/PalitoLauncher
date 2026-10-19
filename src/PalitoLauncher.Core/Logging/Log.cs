using System.Globalization;
using System.Security.AccessControl;
using System.Text;

namespace PalitoLauncher.Core.Logging;

/// <summary>
/// Logger de archivo minimalista, thread-safe y que NUNCA lanza excepciones.
/// Escribe en &lt;logsDir&gt;\launcher-yyyyMMdd.log, una línea por evento:
/// "2026-10-19 13:45:01.123 [INFO ] [T12] mensaje" (+ excepción completa en líneas siguientes si la hay).
/// Borra logs con más de <c>keepDays</c> días al inicializar. Antes de Initialize, solo escribe a System.Diagnostics.Debug.
/// El archivo se abre en modo append con FileShare.ReadWrite | FileShare.Delete (dos procesos —p.ej. durante la
/// auto-actualización— pueden escribir a la vez y el usuario puede abrirlo mientras corre). Si no se puede abrir, usa
/// "launcher-yyyyMMdd-&lt;pid&gt;.log". Cambio de día → archivo nuevo.
/// </summary>
public static class Log
{
    private static readonly object InitLock = new();
    private static LogSink? _sink;

    /// <summary>Reloj (hora local) usado para las marcas de tiempo y la rotación diaria. Inyectable en tests.</summary>
    internal static Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>Inicializa el logger. Seguro llamarlo más de una vez (la segunda no hace nada).</summary>
    public static void Initialize(string logsDir, int keepDays = 7)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logsDir))
            {
                WriteDebugOutput("Log.Initialize: carpeta de logs vacía; solo se escribe a Debug.");
                return;
            }

            LogSink sink;
            lock (InitLock)
            {
                if (_sink != null)
                    return;
                sink = new LogSink(logsDir, () => Clock(), Environment.ProcessId);
                Volatile.Write(ref _sink, sink);
            }

            LogSink.CleanupOldLogs(sink.LogsDirectory, keepDays, SafeNow());
        }
        catch (Exception ex)
        {
            WriteDebugOutput("Log.Initialize falló: " + ex);
        }
    }

    /// <summary>Ruta del archivo de log actual (null si no está inicializado).</summary>
    public static string? CurrentLogFile
    {
        get
        {
            try { return Volatile.Read(ref _sink)?.CurrentFile; }
            catch (Exception ex) { WriteDebugOutput("Log.CurrentLogFile: " + ex.Message); return null; }
        }
    }

    /// <summary>Carpeta de logs (null si no está inicializado).</summary>
    public static string? LogsDirectory => Volatile.Read(ref _sink)?.LogsDirectory;

    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message, Exception? exception = null) => Write(LogLevel.Warn, message, exception);
    public static void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    /// <summary>Solo para tests: cierra el archivo y vuelve al estado "sin inicializar".</summary>
    internal static void ResetForTests()
    {
        lock (InitLock)
        {
            var old = Interlocked.Exchange(ref _sink, null);
            old?.Dispose();
            Clock = () => DateTime.Now;
        }
    }

    private static void Write(LogLevel level, string? message, Exception? exception)
    {
        try
        {
            var sink = Volatile.Read(ref _sink);
            if (sink != null)
            {
                sink.Write(level, message, exception);
                return;
            }

            WriteDebugOutput(LogSink.Format(SafeNow(), level, message, exception));
        }
        catch (Exception ex)
        {
            // El logger no puede registrar sus propios fallos: como mucho, a la salida de depuración.
            WriteDebugOutput("Log.Write falló: " + ex.Message);
        }
    }

    private static DateTime SafeNow()
    {
        try { return Clock(); }
        catch { return DateTime.Now; }
    }

    internal static void WriteDebugOutput(string text)
    {
        try { System.Diagnostics.Debug.WriteLine(text.TrimEnd('\r', '\n')); }
        catch { /* nada más que hacer */ }
    }
}

internal enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Escritor de un archivo de log diario. Separado de <see cref="Log"/> para poder probarlo con varias instancias
/// (simula dos procesos escribiendo a la vez) y con un reloj inyectado.
/// Abre el archivo SOLO con permiso de anexado (FILE_APPEND_DATA): así el SO coloca cada escritura al final del archivo
/// aunque otro proceso esté escribiendo a la vez (con FileMode.Append normal, cada handle recuerda su propia posición y
/// dos procesos se pisarían las líneas). Cada evento se escribe con una sola llamada y sin búfer.
/// </summary>
internal sealed class LogSink : IDisposable
{
    /// <summary>Por encima de este tamaño solo se escriben WARN/ERROR (protege el disco ante un bucle de logs).</summary>
    internal const long MaxFileBytes = 50L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan ReopenBackoff = TimeSpan.FromSeconds(5);

    private readonly object _sync = new();
    private readonly Func<DateTime> _clock;
    private readonly int _pid;

    private FileStream? _stream;
    private DateTime _streamDate;
    private string? _currentFile;
    private long _approxLength;
    private bool _sizeLimitNoticeWritten;
    private DateTime _nextOpenAttemptUtc = DateTime.MinValue;
    private bool _disposed;

    public LogSink(string logsDir, Func<DateTime> clock, int pid)
    {
        LogsDirectory = Path.GetFullPath(logsDir);
        _clock = clock;
        _pid = pid;
        try { Directory.CreateDirectory(LogsDirectory); }
        catch (Exception ex) { Log.WriteDebugOutput($"No se pudo crear la carpeta de logs {LogsDirectory}: {ex.Message}"); }
    }

    public string LogsDirectory { get; }

    /// <summary>Archivo abierto ahora mismo o, si aún no se abrió ninguno, el que corresponde a hoy.</summary>
    public string CurrentFile
    {
        get
        {
            lock (_sync)
                return _currentFile ?? GetMainPath(Now().Date);
        }
    }

    public string GetMainPath(DateTime date) =>
        Path.Combine(LogsDirectory, "launcher-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    public string GetFallbackPath(DateTime date) =>
        Path.Combine(LogsDirectory, "launcher-" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
            _pid.ToString(CultureInfo.InvariantCulture) + ".log");

    public void Write(LogLevel level, string? message, Exception? exception)
    {
        var now = Now();
        var text = Format(now, level, message, exception);
        try
        {
            lock (_sync)
            {
                if (_disposed || !EnsureStream(now.Date))
                {
                    Log.WriteDebugOutput(text);
                    return;
                }

                if (_approxLength >= MaxFileBytes && level < LogLevel.Warn)
                {
                    if (!_sizeLimitNoticeWritten)
                    {
                        _sizeLimitNoticeWritten = true;
                        TryWriteBytes(Utf8NoBom.GetBytes(Format(now, LogLevel.Warn,
                            "El log de hoy es demasiado grande: a partir de aquí solo se registran avisos y errores.", null)), now.Date);
                    }
                    return;
                }

                if (!TryWriteBytes(Utf8NoBom.GetBytes(text), now.Date))
                    Log.WriteDebugOutput(text);
            }
        }
        catch (Exception ex)
        {
            Log.WriteDebugOutput("LogSink.Write falló: " + ex.Message);
        }
    }

    /// <summary>Formatea un evento (con salto de línea final).</summary>
    public static string Format(DateTime time, LogLevel level, string? message, Exception? exception)
    {
        var sb = new StringBuilder(96 + (message?.Length ?? 0));
        sb.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(LevelText(level)).Append("] [T");
        sb.Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ");
        sb.Append(message);
        if (exception != null)
        {
            sb.Append(Environment.NewLine);
            string details;
            try { details = exception.ToString(); }
            catch (Exception toStringError) { details = exception.GetType().FullName + ": " + exception.Message + " (ToString falló: " + toStringError.Message + ")"; }
            sb.Append(details);
        }
        sb.Append(Environment.NewLine);
        return sb.ToString();
    }

    /// <summary>Borra "launcher-*.log" con más de <paramref name="keepDays"/> días (por fecha de modificación). Nunca lanza.</summary>
    public static void CleanupOldLogs(string logsDir, int keepDays, DateTime now)
    {
        try
        {
            if (!Directory.Exists(logsDir))
                return;
            var limit = now.AddDays(-Math.Max(1, keepDays));
            foreach (var file in Directory.EnumerateFiles(logsDir, "launcher-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < limit)
                        File.Delete(file);
                }
                catch (Exception ex)
                {
                    Log.WriteDebugOutput($"No se pudo borrar el log antiguo {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.WriteDebugOutput("Limpieza de logs antiguos falló: " + ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            CloseStream();
        }
    }

    private DateTime Now()
    {
        try { return _clock(); }
        catch { return DateTime.Now; }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    // Escribe un bloque; si falla (disco retirado, archivo borrado…) reabre una vez y reintenta.
    private bool TryWriteBytes(byte[] bytes, DateTime date)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_stream == null && !EnsureStream(date, ignoreBackoff: attempt > 0))
                return false;
            try
            {
                _stream!.Write(bytes, 0, bytes.Length);
                _approxLength += bytes.Length;
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteDebugOutput("Escritura de log falló: " + ex.Message);
                CloseStream();
            }
        }
        return false;
    }

    private bool EnsureStream(DateTime date, bool ignoreBackoff = false)
    {
        if (_stream != null && _streamDate == date)
            return true;

        // Cambio de día (o primer uso): archivo nuevo.
        CloseStream();
        if (!ignoreBackoff && DateTime.UtcNow < _nextOpenAttemptUtc)
            return false;

        try { Directory.CreateDirectory(LogsDirectory); }
        catch (Exception ex) { Log.WriteDebugOutput("No se pudo crear la carpeta de logs: " + ex.Message); }

        foreach (var candidate in new[] { GetMainPath(date), GetFallbackPath(date) })
        {
            try
            {
                var stream = OpenAppendOnly(candidate);
                _stream = stream;
                _streamDate = date;
                _currentFile = candidate;
                _sizeLimitNoticeWritten = false;
                try { _approxLength = stream.Length; }
                catch { _approxLength = 0; }
                return true;
            }
            catch (Exception ex)
            {
                Log.WriteDebugOutput($"No se pudo abrir el log {candidate}: {ex.Message}");
            }
        }

        _nextOpenAttemptUtc = DateTime.UtcNow + ReopenBackoff;
        return false;
    }

    // Ojo: con FileMode.Append .NET exige derechos "solo escritura" (nada de ReadAttributes/ReadData).
    private static FileStream OpenAppendOnly(string path) =>
        new FileInfo(path).Create(
            FileMode.Append,
            FileSystemRights.AppendData | FileSystemRights.Synchronize,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None,
            fileSecurity: null);

    private void CloseStream()
    {
        var stream = _stream;
        _stream = null;
        if (stream == null)
            return;
        try { stream.Dispose(); }
        catch (Exception ex) { Log.WriteDebugOutput("Cierre del log falló: " + ex.Message); }
    }
}
