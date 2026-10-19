using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Util;

namespace PalitoLauncher.Core.Launch;

/// <summary>
/// Lanza el juego y sigue su proceso para que la UI sepa si está abierto.
/// Los eventos se disparan en hilos del pool: la UI debe pasar al Dispatcher.
/// La ruta de la imagen de cada proceso se lee con QueryFullProcessImageNameW + PROCESS_QUERY_LIMITED_INFORMATION
/// (funciona también con procesos elevados, a diferencia de Process.MainModule).
/// Orden de bloqueos: nunca se llama a Dispose/Kill/HasExited/WaitForExit del Process seguido con _lock tomado (.NET dispara
/// Exited con el lock del propio Process tomado y el manejador toma _lock).
/// </summary>
public sealed class GameProcessManager : IDisposable
{
    // Procesos auxiliares de Unity que también bloquean la carpeta del juego.
    private static readonly string[] CrashHandlerNames = { "UnityCrashHandler64", "UnityCrashHandler32" };

    private static readonly TimeSpan KillWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private Process? _process;

    // Se abre cuando ya se disparó GameStarted del proceso seguido: GameExited nunca llega antes que GameStarted.
    private ManualResetEventSlim? _startedGate;
    private bool _disposed;

    public GameProcessManager()
    {
    }

    /// <summary>Se dispara cuando el juego arranca (Launch) o cuando se engancha a uno ya abierto (TryAttachToRunning).</summary>
    public event EventHandler? GameStarted;

    /// <summary>Se dispara cuando el proceso seguido termina. Argumento: código de salida (-1 si no se pudo leer).</summary>
    public event EventHandler<int>? GameExited;

    /// <summary>True mientras el proceso seguido siga vivo.</summary>
    public bool IsRunning
    {
        get
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var tracked = GetTracked();
                if (tracked == null)
                    return false;

                // HasExited fuera de _lock (ver orden de bloqueos).
                var alive = !HasExitedSafe(tracked);
                lock (_lock)
                {
                    // Si mientras tanto se soltó o cambió el proceso, el resultado no vale: se repite.
                    if (ReferenceEquals(_process, tracked))
                        return alive;
                }
            }

            return GetTracked() != null;
        }
    }

    /// <summary>
    /// Arranca &lt;gameDir&gt;\&lt;executableName&gt; con WorkingDirectory = gameDir, UseShellExecute = false y los argumentos
    /// dados (tal cual, pueden ser null/vacíos). Habilita EnableRaisingEvents y sigue el proceso.
    /// Si ya hay un juego seguido en ejecución → LauncherException ("El juego ya está abierto.").
    /// Exe inexistente → LauncherException ("No se encuentra Coop2D.exe. Usa «Reparar»."); Win32Exception (p.ej. bloqueado
    /// por el antivirus) → LauncherException con mensaje claro.
    /// </summary>
    public void Launch(string gameDir, string executableName, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var exePath = Path.Combine(gameDir, executableName);
        if (!File.Exists(exePath))
        {
            Log.Warn($"No se encuentra el ejecutable del juego: {exePath}");
            throw new LauncherException($"No se encuentra {executableName}. Usa «Reparar».");
        }

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            Arguments = arguments ?? "",
        };

        // HasExited del proceso anterior fuera de _lock (ver orden de bloqueos).
        var current = GetTracked();
        if (current != null && !HasExitedSafe(current))
            throw new LauncherException("El juego ya está abierto.");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;
        var gate = new ManualResetEventSlim(false);

        Process? old = null;
        var rejected = false;
        lock (_lock)
        {
            if (_disposed || (_process != null && !ReferenceEquals(_process, current)))
            {
                // Se cerró el gestor o se empezó a seguir otro proceso mientras tanto.
                rejected = true;
            }
            else
            {
                // Se registra antes de Start para que un Exited inmediato encuentre el proceso seguido.
                old = DetachTrackedProcess();
                _process = process;
                _startedGate = gate;
            }
        }

        if (rejected)
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new LauncherException("El juego ya está abierto.");
        }

        DisposeDetached(old);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                    _startedGate = null;
                }
            }

            gate.Set();
            process.Exited -= OnProcessExited;
            process.Dispose();

            if (ex is Win32Exception w32)
            {
                Log.Error($"No se pudo iniciar {exePath} (Win32 {w32.NativeErrorCode}).", ex);
                throw new LauncherException(GetStartErrorMessage(executableName, w32.NativeErrorCode), ex);
            }

            Log.Error($"No se pudo iniciar {exePath}.", ex);
            throw new LauncherException($"No se pudo abrir {executableName}. Vuelve a intentarlo; si se repite, usa «Reparar».", ex);
        }

        Log.Info($"Juego iniciado: {exePath} (pid {SafePid(process)}) args=\"{arguments}\"");
        RaiseGameStarted(gate);
    }

    /// <summary>
    /// Si hay un proceso del juego ejecutándose desde <paramref name="gameDir"/> (abierto fuera del launcher o antes de
    /// reiniciarlo), se engancha a él (eventos incluidos) y devuelve true. Nunca lanza.
    /// </summary>
    public bool TryAttachToRunning(string gameDir, string executableName)
    {
        try
        {
            if (_disposed || string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(executableName))
                return false;

            // Ya seguimos uno vivo desde esa carpeta: no hace falta buscar (comprobado fuera de _lock).
            var tracked = GetTracked();
            if (tracked != null && !HasExitedSafe(tracked))
            {
                var trackedPath = TryGetProcessImagePath(SafePid(tracked));
                if (trackedPath == null || IsInside(trackedPath, gameDir))
                    return true;
            }

            var name = Path.GetFileNameWithoutExtension(executableName);
            var candidates = Process.GetProcessesByName(name);
            Process? attached = null;
            ManualResetEventSlim? gate = null;
            try
            {
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var imagePath = TryGetProcessImagePath(candidate.Id);
                        if (imagePath == null || !IsInside(imagePath, gameDir))
                            continue;

                        if (TryAttach(candidate, out gate))
                        {
                            attached = candidate;
                            Log.Info($"Enganchado al juego ya abierto: {imagePath} (pid {candidate.Id})");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"No se pudo comprobar el proceso {name} (pid {SafePid(candidate)}).", ex);
                    }
                }
            }
            finally
            {
                // El proceso enganchado lo libera el manejador de Exited o Dispose.
                foreach (var candidate in candidates)
                {
                    if (!ReferenceEquals(candidate, attached))
                        candidate.Dispose();
                }
            }

            if (attached == null || gate == null)
                return false;

            RaiseGameStarted(gate);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Error buscando el juego en ejecución.", ex);
            return false;
        }
    }

    /// <summary>
    /// Fuerza el cierre del juego seguido (Kill(entireProcessTree: true)) y de cualquier otro proceso del juego o de
    /// UnityCrashHandler que se ejecute desde <paramref name="gameDir"/>; espera hasta 5 s a que terminen. Acceso denegado →
    /// LauncherException("No se pudo cerrar el juego. Ciérralo desde el Administrador de tareas."). Los procesos con ruta
    /// ilegible no se tocan; los de otra sesión de Windows (otro usuario) tampoco cuentan en la comprobación final
    /// (<see cref="IsRunningFrom"/>).
    /// </summary>
    public void Kill(string gameDir, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        const string failMessage = "No se pudo cerrar el juego. Ciérralo desde el Administrador de tareas.";
        var deadline = Stopwatch.StartNew();
        var accessDenied = false;
        var victims = new List<Process>();

        try
        {
            // Se copia la referencia y se trabaja fuera de _lock (ver orden de bloqueos).
            var tracked = GetTracked();

            // Se guarda el pid antes de matar: el manejador de Exited puede liberar el objeto Process.
            var trackedPid = tracked != null ? SafePid(tracked) : -1;
            if (tracked != null && !HasExitedSafe(tracked))
            {
                Log.Info($"Forzando el cierre del juego (pid {SafePid(tracked)}).");
                if (!TryKill(tracked))
                    accessDenied = true;
            }

            foreach (var p in GetWatchedProcesses(executableName))
            {
                var keep = false;
                try
                {
                    if (trackedPid > 0 && SafePid(p) == trackedPid)
                        continue;

                    var imagePath = TryGetProcessImagePath(p.Id);
                    // Sin ruta legible no se mata: podría ser otra copia del juego. Se valida al final con IsRunningFrom.
                    if (imagePath == null || !IsInside(imagePath, gameDir))
                        continue;

                    Log.Info($"Forzando el cierre de {imagePath} (pid {p.Id}).");
                    if (!TryKill(p))
                        accessDenied = true;
                    keep = true;
                    victims.Add(p);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Error cerrando un proceso del juego (pid {SafePid(p)}).", ex);
                }
                finally
                {
                    if (!keep)
                        p.Dispose();
                }
            }

            // Espera común de hasta 5 s para todos.
            if (tracked != null)
                WaitForExitSafe(tracked, Remaining(deadline));
            foreach (var p in victims)
                WaitForExitSafe(p, Remaining(deadline));
        }
        finally
        {
            foreach (var p in victims)
                p.Dispose();
        }

        if (accessDenied || IsRunningFrom(gameDir, executableName))
        {
            Log.Warn($"El juego sigue abierto tras intentar cerrarlo ({gameDir}).");
            throw new LauncherException(failMessage);
        }
    }

    /// <summary>
    /// ¿Hay algún proceso del juego (&lt;executableName&gt;) o UnityCrashHandler64.exe / UnityCrashHandler32.exe cuya imagen esté
    /// dentro de <paramref name="gameDir"/>? Si no se puede leer la ruta de un proceso vivo con el mismo nombre que el juego
    /// (acceso denegado), se considera que SÍ (conservador, p.ej. una copia elevada del juego), salvo que ese proceso sea de
    /// otra sesión de Windows (otro usuario con cambio rápido de usuario): esos se ignoran. Nunca lanza.
    /// </summary>
    public static bool IsRunningFrom(string gameDir, string executableName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || string.IsNullOrWhiteSpace(executableName))
                return false;

            var gameName = Path.GetFileNameWithoutExtension(executableName);
            var candidates = GetWatchedProcesses(executableName);
            try
            {
                foreach (var p in candidates)
                {
                    try
                    {
                        if (HasExitedSafe(p))
                            continue;

                        var isGame = string.Equals(p.ProcessName, gameName, StringComparison.OrdinalIgnoreCase);
                        var imagePath = TryGetProcessImagePath(p.Id);
                        if (imagePath == null)
                        {
                            var pid = p.Id;
                            var session = TryGetSessionId(p);
                            if (IsUnreadableProcessBlocking(isGame, session, CurrentSessionId, () => IsAlive(pid)))
                            {
                                Log.Info($"No se pudo leer la ruta de {p.ProcessName} (pid {pid}); se asume que es el juego.");
                                return true;
                            }

                            if (isGame && session != null && CurrentSessionId != null && session != CurrentSessionId)
                                Log.Debug($"Se ignora {p.ProcessName} (pid {pid}) de otra sesión de Windows ({session}).");
                            continue;
                        }

                        if (IsInside(imagePath, gameDir))
                            return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Error comprobando un proceso del juego (pid {SafePid(p)}).", ex);
                    }
                }

                return false;
            }
            finally
            {
                foreach (var p in candidates)
                    p.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Error comprobando si el juego está abierto.", ex);
            return false;
        }
    }

    public void Dispose()
    {
        Process? old;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            old = DetachTrackedProcess();
        }

        DisposeDetached(old);
    }

    // ---------------------------------------------------------------- internos

    private bool TryAttach(Process candidate, out ManualResetEventSlim? gate)
    {
        var newGate = new ManualResetEventSlim(false);
        gate = null;
        Process? old;
        lock (_lock)
        {
            if (_disposed)
                return false;

            old = DetachTrackedProcess();
            _process = candidate;
            _startedGate = newGate;
        }

        DisposeDetached(old);

        try
        {
            candidate.Exited += OnProcessExited;
            // Si ya terminó, .NET dispara Exited enseguida.
            candidate.EnableRaisingEvents = true;
            gate = newGate;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo seguir el proceso del juego (pid {SafePid(candidate)}).", ex);
            candidate.Exited -= OnProcessExited;
            lock (_lock)
            {
                if (ReferenceEquals(_process, candidate))
                {
                    _process = null;
                    _startedGate = null;
                }
            }

            newGate.Set();
            return false;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
            return;

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo leer el código de salida del juego: {ex.Message}");
            exitCode = -1;
        }

        bool wasTracked;
        ManualResetEventSlim? gate = null;
        lock (_lock)
        {
            wasTracked = !_disposed && ReferenceEquals(_process, process);
            if (wasTracked)
            {
                gate = _startedGate;
                _process = null;
                _startedGate = null;
            }
        }

        process.Exited -= OnProcessExited;
        try
        {
            process.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"Error liberando el proceso del juego: {ex.Message}");
        }

        if (!wasTracked)
            return;

        // Si el juego murió nada más arrancar, se espera a que GameStarted se haya disparado.
        if (gate != null && !gate.Wait(TimeSpan.FromSeconds(5)))
            Log.Warn("GameStarted tardó demasiado; se notifica el cierre igualmente.");

        Log.Info($"El juego se cerró (código {exitCode}).");
        try
        {
            GameExited?.Invoke(this, exitCode);
        }
        catch (Exception ex)
        {
            // Estamos en un hilo del pool: una excepción aquí tumbaría la app.
            Log.Error("Error en un manejador de GameExited.", ex);
        }
    }

    private void RaiseGameStarted(ManualResetEventSlim gate)
    {
        try
        {
            GameStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Error en un manejador de GameStarted.", ex);
        }
        finally
        {
            gate.Set();
        }
    }

    private Process? GetTracked()
    {
        lock (_lock)
        {
            return _process;
        }
    }

    /// <summary>
    /// Deja de seguir el proceso actual (solo cambia campos). Llamar con _lock tomado y pasar el resultado a
    /// <see cref="DisposeDetached"/> DESPUÉS de soltarlo.
    /// </summary>
    private Process? DetachTrackedProcess()
    {
        var old = _process;
        _process = null;
        _startedGate?.Set();
        _startedGate = null;
        return old;
    }

    /// <summary>Libera (sin cerrarlo) un proceso que ya no se sigue. Llamar SIN _lock tomado.</summary>
    private void DisposeDetached(Process? old)
    {
        if (old == null)
            return;

        try
        {
            old.Exited -= OnProcessExited;
            old.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug($"Error liberando el proceso anterior: {ex.Message}");
        }
    }

    private static readonly Lazy<int?> s_currentSessionId = new(() =>
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo leer la sesión de Windows actual: {ex.Message}");
            return null;
        }
    });

    /// <summary>Sesión de Windows de este proceso (null si no se pudo leer).</summary>
    internal static int? CurrentSessionId => s_currentSessionId.Value;

    /// <summary>Sesión de Windows del proceso (no necesita abrirlo); null si no se pudo leer.</summary>
    private static int? TryGetSessionId(Process p)
    {
        try
        {
            return p.SessionId;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Para un proceso cuya ruta no se puede leer: ¿hay que suponer que es el juego? Solo si se llama como el juego, sigue
    /// vivo y no es de otra sesión de Windows (un proceso de otro usuario no puede estar usando nuestra carpeta y no lo
    /// podríamos cerrar). Sesión desconocida → se trata como la nuestra (conservador).
    /// </summary>
    internal static bool IsUnreadableProcessBlocking(bool isGame, int? processSessionId, int? currentSessionId, Func<bool> isAlive)
    {
        if (!isGame)
            return false;

        if (processSessionId != null && currentSessionId != null && processSessionId != currentSessionId)
            return false;

        return isAlive();
    }

    /// <summary>
    /// Procesos llamados como el juego o como UnityCrashHandler, con una sola enumeración del sistema.
    /// El llamador debe liberarlos.
    /// </summary>
    private static List<Process> GetWatchedProcesses(string executableName)
    {
        var names = new HashSet<string>(CrashHandlerNames, StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileNameWithoutExtension(executableName),
        };

        var result = new List<Process>();
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo enumerar los procesos.", ex);
            return result;
        }

        foreach (var p in all)
        {
            bool match;
            try
            {
                match = names.Contains(p.ProcessName);
            }
            catch (Exception)
            {
                match = false;
            }

            if (match)
                result.Add(p);
            else
                p.Dispose();
        }

        return result;
    }

    private static bool IsInside(string imagePath, string gameDir)
    {
        try
        {
            return FileSystemUtil.IsSubPathOf(imagePath, gameDir);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo comparar rutas ({imagePath} / {gameDir}).", ex);
            return false;
        }
    }

    private static bool TryKill(Process p)
    {
        try
        {
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Ya había terminado.
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo cerrar el proceso (pid {SafePid(p)}).", ex);
            return HasExitedSafe(p);
        }
    }

    private static void WaitForExitSafe(Process p, TimeSpan timeout)
    {
        try
        {
            p.WaitForExit((int)Math.Max(0, timeout.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            Log.Debug($"WaitForExit falló (pid {SafePid(p)}): {ex.Message}");
        }
    }

    private static TimeSpan Remaining(Stopwatch sw)
    {
        var left = KillWaitTimeout - sw.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static bool HasExitedSafe(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch (InvalidOperationException)
        {
            // Proceso no asociado: se trata como terminado.
            return true;
        }
        catch (Exception)
        {
            // Sin acceso (p.ej. elevado): se asume vivo.
            return false;
        }
    }

    private static int SafePid(Process p)
    {
        try
        {
            return p.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static string GetStartErrorMessage(string executableName, int nativeError) => nativeError switch
    {
        2 or 3 => $"No se encuentra {executableName}. Usa «Reparar».",
        5 => $"Windows o el antivirus bloqueó {executableName}. Revisa la cuarentena del antivirus o usa «Reparar».",
        225 or 226 => $"El antivirus bloqueó {executableName} (lo considera peligroso). Revisa la cuarentena del antivirus y usa «Reparar».",
        193 or 216 => $"{executableName} está dañado o no es compatible con este equipo. Usa «Reparar».",
        740 => $"{executableName} pide permisos de administrador y no se pudo abrir.",
        1223 => "Se canceló la apertura del juego.",
        _ => $"No se pudo abrir {executableName} (error {nativeError}). Puede que el antivirus lo esté bloqueando; prueba «Reparar».",
    };

    // ---------------------------------------------------------------- P/Invoke

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ErrorInsufficientBuffer = 122;
    private const uint WaitTimeout = 0x102;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, [Out] char[] buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    /// <summary>Ruta completa del exe de un proceso, o null si no se puede leer (acceso denegado, ya terminó…).</summary>
    internal static string? TryGetProcessImagePath(int processId)
    {
        try
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle.IsInvalid)
                return null;

            var capacity = 1024;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var buffer = new char[capacity];
                var size = buffer.Length;
                if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                    return new string(buffer, 0, size);

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    return null;
                capacity = 32768;
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug($"QueryFullProcessImageName falló para pid {processId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>¿Sigue vivo el pid? Si no se puede abrir con SYNCHRONIZE se asume vivo (conservador).</summary>
    private static bool IsAlive(int processId)
    {
        try
        {
            using var handle = OpenProcess(Synchronize, false, processId);
            if (handle.IsInvalid)
            {
                // ERROR_INVALID_PARAMETER (87) = el pid ya no existe.
                return Marshal.GetLastWin32Error() != 87;
            }

            return WaitForSingleObject(handle, 0) == WaitTimeout;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
