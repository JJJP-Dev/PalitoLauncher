using System.Diagnostics;
using System.IO;
using System.Windows;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Logging;
using PalitoLauncher.Core.Update;

namespace PalitoLauncher.Services;

/// <summary>
/// Instancia única del launcher (por sesión de usuario). La instancia principal tiene el Mutex
/// <see cref="MutexName"/> y escucha <see cref="ActivateEventName"/> desde que lo toma (las peticiones que llegan antes de
/// que exista la ventana se guardan y se atienden en <see cref="StartListening"/>); al atender una trae su ventana al
/// frente y confirma con <see cref="SignalActivated"/>: señala el evento propio de cada instancia que espera
/// ("<see cref="ActivatedEventName"/>.&lt;pid&gt;") y también <see cref="ActivatedEventName"/> (compatibilidad con versiones
/// anteriores). Una segunda instancia crea su evento propio, señala Activate y espera el "ack" 3 s; si no llega, insiste y
/// espera hasta 10 s más (la principal puede estar arrancando en un PC lento). Solo entonces, si la otra sigue con el mutex,
/// ofrece cerrarla. Con "--post-update &lt;pid&gt;" espera a que la versión vieja termine. Nunca se ejecuta sin el mutex.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\PalitoLauncher.SingleInstance";
    public const string ActivateEventName = @"Local\PalitoLauncher.Activate";
    public const string ActivatedEventName = @"Local\PalitoLauncher.Activated";

    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AckRetryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PostUpdateWait = TimeSpan.FromSeconds(30);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _activated;
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _handlerLock = new();
    private Thread? _listener;
    private Action? _onActivate;
    private bool _pendingActivation;
    private volatile bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, EventWaitHandle activated)
    {
        _mutex = mutex;
        _activate = activate;
        _activated = activated;
    }

    /// <summary>
    /// Intenta convertirse en la instancia principal. Devuelve null si este proceso debe cerrarse (ya había otra
    /// instancia, que se ha traído al frente, o el usuario no quiso cerrarla). Llamar en el hilo de UI (el mutex
    /// pertenece al hilo que lo adquiere y se libera en <see cref="Dispose"/> desde ese mismo hilo).
    /// </summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (TryWait(mutex, TimeSpan.Zero)) return Create(mutex);

            if (GetPostUpdatePid(args) is int pid)
            {
                Log.Info($"Arranque tras auto-actualización: esperando a que termine el proceso {pid}.");
                WaitForProcessExit(pid, PostUpdateWait);
                if (TryWait(mutex, TimeSpan.FromSeconds(5))) return Create(mutex);

                Log.Warn("Tras la actualización sigue abierta otra instancia; se le pide que se muestre y este proceso se cierra.");
                SignalActivate(waitForAck: false);
                mutex.Dispose();
                return null;
            }

            if (SignalActivate(waitForAck: true))
            {
                Log.Info("Ya había una instancia abierta: se ha traído al frente y este proceso se cierra.");
                mutex.Dispose();
                return null;
            }

            // Mientras esperábamos, la otra pudo cerrarse.
            if (TryWait(mutex, TimeSpan.Zero)) return Create(mutex);

            Log.Warn("Hay otra instancia del launcher que no responde.");
            var answer = MessageBox.Show(
                "Palito Launcher ya está abierto pero no responde. ¿Cerrarlo y volver a abrirlo?",
                LauncherConfig.LauncherName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                mutex.Dispose();
                return null;
            }

            KillOtherInstances();
            if (TryWait(mutex, TimeSpan.FromSeconds(10))) return Create(mutex);

            Log.Error("No se pudo tomar el control tras cerrar la otra instancia.");
            MessageBox.Show(
                "No se pudo cerrar la otra ventana de Palito Launcher. Ciérrala desde el Administrador de tareas y vuelve a intentarlo.",
                LauncherConfig.LauncherName, MessageBoxButton.OK, MessageBoxImage.Error);
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Indica qué hacer con las peticiones de activación (llamar cuando la ventana ya existe). La escucha empezó al tomar el
    /// mutex: si llegó alguna petición antes, se atiende ahora (una sola vez). <paramref name="onActivate"/> se llama en un
    /// hilo de fondo; debe pasar al Dispatcher, traer la ventana al frente y llamar a <see cref="SignalActivated"/>.
    /// Solo cuenta la primera llamada.
    /// </summary>
    public void StartListening(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        if (_disposed) return;

        bool flush;
        lock (_handlerLock)
        {
            if (_onActivate != null) return;
            _onActivate = onActivate;
            flush = _pendingActivation;
            _pendingActivation = false;
        }

        EnsureListening();
        if (flush)
        {
            Log.Info("Se atiende la petición de mostrar la ventana que llegó durante el arranque.");
            ThreadPool.QueueUserWorkItem(_ => InvokeHandler(onActivate));
        }
    }

    /// <summary>
    /// Confirma a las instancias que esperan que la ventana ya está al frente: a cada una por su evento propio y, por
    /// compatibilidad, el evento común. Nunca lanza.
    /// </summary>
    public void SignalActivated()
    {
        if (_disposed) return;
        try
        {
            _activated.Set();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo confirmar la activación a la otra instancia.", ex);
        }

        // Recorrer procesos lleva unos ms: fuera del hilo de UI.
        try
        {
            ThreadPool.QueueUserWorkItem(_ => AckWaitingInstances());
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo confirmar la activación a las otras instancias.", ex);
        }
    }

    public void Dispose()
    {
        lock (_handlerLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _stop.Set(); } catch (Exception ex) { Log.Warn("No se pudo detener la escucha de instancia única.", ex); }
        try { _listener?.Join(TimeSpan.FromSeconds(1)); } catch (Exception ex) { Log.Warn("Error al esperar el hilo de instancia única.", ex); }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            // Si se libera desde otro hilo falla; el SO lo libera igualmente al terminar el proceso.
            Log.Debug($"No se pudo liberar el mutex de instancia única: {ex.Message}");
        }

        _mutex.Dispose();
        _activate.Dispose();
        _activated.Dispose();
        _stop.Dispose();
    }

    /// <summary>Arranca el hilo de escucha si aún no corre. Nunca lanza.</summary>
    private void EnsureListening()
    {
        lock (_handlerLock)
        {
            if (_disposed || _listener != null) return;
            try
            {
                var thread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "PalitoLauncher.SingleInstance",
                };
                thread.Start();
                _listener = thread;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo empezar a escuchar a otras instancias.", ex);
            }
        }
    }

    private void ListenLoop()
    {
        var handles = new WaitHandle[] { _activate, _stop };
        while (true)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (Exception ex)
            {
                if (!_disposed) Log.Error("La escucha de instancia única se detuvo por un error.", ex);
                return;
            }

            if (index != 0) return;

            Log.Info("Otra instancia pidió mostrar la ventana.");
            Action? handler;
            lock (_handlerLock)
            {
                handler = _onActivate;
                // Sin ventana todavía: se guarda y se atiende en StartListening (el ack se envía entonces).
                if (handler == null) _pendingActivation = true;
            }

            if (handler != null) InvokeHandler(handler);
        }
    }

    private static void InvokeHandler(Action handler)
    {
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            Log.Error("Error al procesar la activación desde otra instancia.", ex);
        }
    }

    private static SingleInstance Create(Mutex mutex)
    {
        // Activate NO se reinicia: una petición que llegue justo ahora se guarda y se atiende al haber ventana.
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var activated = new EventWaitHandle(false, EventResetMode.AutoReset, ActivatedEventName);
        try
        {
            // Ack común viejo que nadie consumió.
            activated.Reset();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo reiniciar el evento de confirmación de instancia única.", ex);
        }

        var instance = new SingleInstance(mutex, activate, activated);
        // Se escucha desde ya, antes de crear la ventana.
        instance.EnsureListening();
        return instance;
    }

    /// <summary>Nombre del evento de confirmación propio de la instancia secundaria <paramref name="pid"/>.</summary>
    private static string GetAckEventName(int pid) => ActivatedEventName + "." + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Señala el evento propio de cada instancia que está esperando confirmación. Nunca lanza.</summary>
    private static void AckWaitingInstances()
    {
        Process[] all;
        try
        {
            all = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo enumerar los procesos para confirmar la activación.", ex);
            return;
        }

        var self = Environment.ProcessId;
        foreach (var process in all)
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    if (pid == self) continue;
                    if (!EventWaitHandle.TryOpenExisting(GetAckEventName(pid), out var ack)) continue;
                    using (ack)
                    {
                        ack.Set();
                    }
                    Log.Info($"Activación confirmada a la instancia {pid}.");
                }
                catch (Exception)
                {
                    // Sin acceso o ya se cerró: se ignora.
                }
            }
        }
    }

    private static bool TryWait(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // La instancia anterior murió sin liberarlo: ahora es nuestro.
            Log.Info("El mutex de instancia única estaba abandonado (la instancia anterior se cerró de golpe).");
            return true;
        }
    }

    /// <summary>
    /// Señala Activate y, si se pide, espera el ack (propio o común): 3 s y, si no llega, insiste y espera hasta 10 s más.
    /// Nunca lanza.
    /// </summary>
    private static bool SignalActivate(bool waitForAck)
    {
        try
        {
            // Evento propio (ManualReset): no se pierde aunque la principal confirme antes de que empecemos a esperar, y otra
            // instancia que espere a la vez no nos lo quita.
            using var ownAck = new EventWaitHandle(false, EventResetMode.ManualReset, GetAckEventName(Environment.ProcessId));
            ownAck.Reset();
            using var activated = new EventWaitHandle(false, EventResetMode.AutoReset, ActivatedEventName);
            using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activated.Reset();
            NativeMethods.AllowAnyProcessToSetForeground();
            activate.Set();
            if (!waitForAck) return true;

            var acks = new WaitHandle[] { ownAck, activated };
            if (WaitHandle.WaitAny(acks, AckTimeout) != WaitHandle.WaitTimeout) return true;

            // Puede estar arrancando todavía: se insiste (por si la señal se juntó con la de otra instancia) y se espera más.
            Log.Info($"La otra instancia no confirmó en {AckTimeout.TotalSeconds:0} s; se espera hasta {AckRetryTimeout.TotalSeconds:0} s más.");
            activate.Set();
            return WaitHandle.WaitAny(acks, AckRetryTimeout) != WaitHandle.WaitTimeout;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo avisar a la otra instancia.", ex);
            return false;
        }
    }

    private static int? GetPostUpdatePid(string[] args)
    {
        if (args == null) return null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], LauncherSelfUpdater.PostUpdateArgument, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid) && pid > 0)
            {
                return pid;
            }
        }
        return null;
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.WaitForExit(timeout))
                Log.Warn($"El proceso {pid} sigue abierto tras {timeout.TotalSeconds:0} s.");
        }
        catch (ArgumentException)
        {
            // Ya había terminado.
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo esperar al proceso {pid}.", ex);
        }
    }

    /// <summary>Mata los procesos con la misma ruta de exe que este (excepto el propio). Nunca lanza.</summary>
    private static void KillOtherInstances()
    {
        var selfPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(selfPath))
        {
            Log.Warn("No se conoce la ruta del exe actual; no se cierra ninguna instancia.");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(selfPath);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Ejecutándose con dotnet (desarrollo): no se cierran otros procesos.");
            return;
        }

        string selfFull;
        try { selfFull = Path.GetFullPath(selfPath); }
        catch (Exception ex)
        {
            Log.Warn("Ruta del exe actual no válida.", ex);
            return;
        }

        Process[] candidates;
        try { candidates = Process.GetProcessesByName(name); }
        catch (Exception ex)
        {
            Log.Warn("No se pudo enumerar los procesos.", ex);
            return;
        }

        foreach (var process in candidates)
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    var path = NativeMethods.TryGetProcessImagePath(process.Id);
                    if (path == null || !string.Equals(Path.GetFullPath(path), selfFull, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Log.Warn($"Cerrando la instancia colgada (pid {process.Id}).");
                    // Sin el árbol: el juego que abrió esa instancia debe seguir abierto.
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"No se pudo cerrar el proceso {process.Id}.", ex);
                }
            }
        }
    }
}
