using System.Diagnostics;
using PalitoLauncher.Core.Launch;
using PalitoLauncher.Core.Util;
using PalitoLauncher.Tests.TestHelpers.F;

namespace PalitoLauncher.Tests;

public class GameProcessManagerTests
{
    private const string FakeExe = "FakeGame.exe";

    private static readonly string CmdPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>Carpeta de juego falsa con una copia de cmd.exe renombrada a FakeGame.exe.</summary>
    private static string CreateFakeGame(TempDir tmp, string folder = "game", string exeName = FakeExe)
    {
        var gameDir = tmp.Combine(folder);
        Directory.CreateDirectory(gameDir);
        File.Copy(CmdPath, Path.Combine(gameDir, exeName), overwrite: true);
        return gameDir;
    }

    /// <summary>Mata cualquier proceso que siga vivo desde la carpeta temporal (por si un test falla a medias).</summary>
    private static void KillLeftovers(string dir)
    {
        foreach (var name in new[] { "FakeGame", "UnityCrashHandler64" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        var path = GameProcessManager.TryGetProcessImagePath(p.Id);
                        if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill(entireProcessTree: true);
                            p.WaitForExit(5000);
                        }
                    }
                    catch (Exception)
                    {
                        // best effort
                    }
                }
            }
        }
    }

    private static Process StartExternal(string exePath, string args) =>
        Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = false, CreateNoWindow = true })!;

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public void IsRunningFrom_EmptyFolder_ReturnsFalse()
    {
        using var tmp = new TempDir();
        Assert.False(GameProcessManager.IsRunningFrom(tmp.Path, FakeExe));
        Assert.False(GameProcessManager.IsRunningFrom(tmp.Path, "Coop2D.exe"));
    }

    [Fact]
    public void IsRunningFrom_InvalidArguments_ReturnsFalse()
    {
        Assert.False(GameProcessManager.IsRunningFrom("", FakeExe));
        Assert.False(GameProcessManager.IsRunningFrom(@"C:\nada", ""));
    }

    [Fact]
    public void Launch_MissingExecutable_ThrowsLauncherException()
    {
        using var tmp = new TempDir();
        using var manager = new GameProcessManager();

        var ex = Assert.Throws<LauncherException>(() => manager.Launch(tmp.Path, "Coop2D.exe", null));
        Assert.Contains("No se encuentra Coop2D.exe", ex.Message);
        Assert.False(manager.IsRunning);
    }

    [Fact]
    public void TryGetProcessImagePath_CurrentProcess_MatchesProcessPath()
    {
        var path = GameProcessManager.TryGetProcessImagePath(Environment.ProcessId);
        Assert.NotNull(path);
        Assert.Equal(Environment.ProcessPath, path, ignoreCase: true);
    }

    [Fact]
    public void TryGetProcessImagePath_NonexistentPid_ReturnsNull()
    {
        // Los pid son múltiplos de 4; uno impar nunca existe.
        Assert.Null(GameProcessManager.TryGetProcessImagePath(int.MaxValue - 2));
    }

    [Fact]
    public async Task Launch_RealProcess_TracksItAndRaisesEvents()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        try
        {
            using var manager = new GameProcessManager();
            var started = 0;
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.GameStarted += (_, _) => Interlocked.Increment(ref started);
            manager.GameExited += (_, code) => exited.TrySetResult(code);

            // ~2 s de vida (ping -n 3).
            manager.Launch(gameDir, FakeExe, "/c ping -n 3 127.0.0.1 >nul");

            Assert.Equal(1, started);
            Assert.True(manager.IsRunning);
            Assert.True(GameProcessManager.IsRunningFrom(gameDir, FakeExe));

            var again = Assert.Throws<LauncherException>(() => manager.Launch(gameDir, FakeExe, null));
            Assert.Equal("El juego ya está abierto.", again.Message);

            var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, code);
            Assert.False(manager.IsRunning);
            await WaitUntilAsync(() => !GameProcessManager.IsRunningFrom(gameDir, FakeExe), TimeSpan.FromSeconds(5));
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public async Task Launch_ExitCodeIsReported()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        try
        {
            using var manager = new GameProcessManager();
            var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.GameStarted += (_, _) =>
            {
                // Un manejador lento no debe dejar que GameExited llegue antes.
                Thread.Sleep(300);
                events.Enqueue("started");
            };
            manager.GameExited += (_, code) =>
            {
                events.Enqueue("exited");
                exited.TrySetResult(code);
            };

            manager.Launch(gameDir, FakeExe, "/c exit 3");

            Assert.Equal(3, await exited.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.False(manager.IsRunning);
            Assert.Equal(new[] { "started", "exited" }, events.ToArray());
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public async Task Kill_StopsTrackedProcessAndRaisesExited()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        try
        {
            using var manager = new GameProcessManager();
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.GameExited += (_, code) => exited.TrySetResult(code);

            manager.Launch(gameDir, FakeExe, "/c ping -n 60 127.0.0.1 >nul");
            Assert.True(manager.IsRunning);

            manager.Kill(gameDir, FakeExe);

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(manager.IsRunning);
            Assert.False(GameProcessManager.IsRunningFrom(gameDir, FakeExe));
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public async Task TryAttachToRunning_AttachesToExternalProcess_AndKillClosesIt()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        try
        {
            using var external = StartExternal(Path.Combine(gameDir, FakeExe), "/c ping -n 60 127.0.0.1 >nul");
            await WaitUntilAsync(() => GameProcessManager.IsRunningFrom(gameDir, FakeExe), TimeSpan.FromSeconds(10));

            using var manager = new GameProcessManager();
            var started = 0;
            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.GameStarted += (_, _) => Interlocked.Increment(ref started);
            manager.GameExited += (_, code) => exited.TrySetResult(code);

            Assert.True(manager.TryAttachToRunning(gameDir, FakeExe));
            Assert.Equal(1, started);
            Assert.True(manager.IsRunning);

            manager.Kill(gameDir, FakeExe);

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(manager.IsRunning);
            Assert.True(external.WaitForExit(5000));
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public async Task ProcessInAnotherFolder_IsNotDetectedNorAttached()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp, "game");
        var otherDir = CreateFakeGame(tmp, "game2");
        try
        {
            using var external = StartExternal(Path.Combine(otherDir, FakeExe), "/c ping -n 60 127.0.0.1 >nul");
            await WaitUntilAsync(() => GameProcessManager.IsRunningFrom(otherDir, FakeExe), TimeSpan.FromSeconds(10));

            // "game2" no está dentro de "game".
            Assert.False(GameProcessManager.IsRunningFrom(gameDir, FakeExe));
            using var manager = new GameProcessManager();
            Assert.False(manager.TryAttachToRunning(gameDir, FakeExe));
            Assert.False(manager.IsRunning);

            // Kill sobre la carpeta sin juego no toca el otro proceso.
            manager.Kill(gameDir, FakeExe);
            Assert.False(external.HasExited);
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public async Task IsRunningFrom_DetectsUnityCrashHandler_AndKillClosesIt()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp, "game", "UnityCrashHandler64.exe");
        try
        {
            using var external = StartExternal(Path.Combine(gameDir, "UnityCrashHandler64.exe"), "/c ping -n 60 127.0.0.1 >nul");
            await WaitUntilAsync(() => GameProcessManager.IsRunningFrom(gameDir, "Coop2D.exe"), TimeSpan.FromSeconds(10));

            using var manager = new GameProcessManager();
            manager.Kill(gameDir, "Coop2D.exe");

            Assert.True(external.WaitForExit(5000));
            Assert.False(GameProcessManager.IsRunningFrom(gameDir, "Coop2D.exe"));
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public void TryAttachToRunning_NothingRunning_ReturnsFalse()
    {
        using var tmp = new TempDir();
        using var manager = new GameProcessManager();
        Assert.False(manager.TryAttachToRunning(tmp.Path, FakeExe));
        Assert.False(manager.TryAttachToRunning("", FakeExe));
        Assert.False(manager.IsRunning);
    }

    // ---------------------------------------------------------------- procesos de otra sesión (otro usuario)

    [Fact]
    public void IsUnreadableProcessBlocking_OtherSession_IsIgnored()
    {
        var aliveCalls = 0;
        bool Alive()
        {
            aliveCalls++;
            return true;
        }

        // Otro usuario (cambio rápido de usuario): no bloquea y ni se comprueba si vive.
        Assert.False(GameProcessManager.IsUnreadableProcessBlocking(isGame: true, processSessionId: 2, currentSessionId: 1, Alive));
        Assert.Equal(0, aliveCalls);

        // Misma sesión (p.ej. juego elevado): conservador, se asume que es el juego si sigue vivo.
        Assert.True(GameProcessManager.IsUnreadableProcessBlocking(isGame: true, processSessionId: 1, currentSessionId: 1, Alive));
        Assert.False(GameProcessManager.IsUnreadableProcessBlocking(isGame: true, processSessionId: 1, currentSessionId: 1, () => false));

        // Sesión desconocida: conservador.
        Assert.True(GameProcessManager.IsUnreadableProcessBlocking(isGame: true, processSessionId: null, currentSessionId: 1, Alive));
        Assert.True(GameProcessManager.IsUnreadableProcessBlocking(isGame: true, processSessionId: 2, currentSessionId: null, Alive));

        // Un UnityCrashHandler ilegible nunca bloquea.
        Assert.False(GameProcessManager.IsUnreadableProcessBlocking(isGame: false, processSessionId: 1, currentSessionId: 1, Alive));
    }

    [Fact]
    public void CurrentSessionId_MatchesThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        Assert.Equal(self.SessionId, GameProcessManager.CurrentSessionId);
    }

    // ---------------------------------------------------------------- orden de bloqueos

    [Fact]
    public async Task Dispose_WhileProcessLockIsHeld_DoesNotBlockOtherCallers()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        using var release = new ManualResetEventSlim(false);
        Thread? holder = null;
        try
        {
            var manager = new GameProcessManager();
            manager.Launch(gameDir, FakeExe, "/c ping -n 60 127.0.0.1 >nul");
            var tracked = (Process)typeof(GameProcessManager)
                .GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(manager)!;

            // Simula el hilo de Exited de .NET, que tiene tomado el lock del propio Process.
            using var held = new ManualResetEventSlim(false);
            holder = new Thread(() =>
            {
                lock (tracked)
                {
                    held.Set();
                    release.Wait();
                }
            }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

            // Dispose puede esperar al lock del Process, pero sin tener tomado el lock del gestor.
            var dispose = Task.Run(manager.Dispose);
            await Task.Delay(300);

            // Con el orden de bloqueos invertido esto se quedaría bloqueado (TimeoutException).
            var isRunning = await Task.Run(() => manager.IsRunning).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(isRunning);

            release.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
            holder?.Join(TimeSpan.FromSeconds(5));
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public void Dispose_ThenLaunch_Throws()
    {
        using var tmp = new TempDir();
        var manager = new GameProcessManager();
        manager.Dispose();
        manager.Dispose(); // idempotente
        Assert.Throws<ObjectDisposedException>(() => manager.Launch(tmp.Path, FakeExe, null));
        Assert.Throws<ObjectDisposedException>(() => manager.LaunchAdditionalInstance(tmp.Path, FakeExe, null));
        Assert.False(manager.TryAttachToRunning(tmp.Path, FakeExe));
    }

    // ---------------------------------------------------------------- instancias extra (modo desarrollador)

    [Fact]
    public void LaunchAdditionalInstance_MissingExecutable_ThrowsLauncherException()
    {
        using var tmp = new TempDir();
        using var manager = new GameProcessManager();

        var ex = Assert.Throws<LauncherException>(() => manager.LaunchAdditionalInstance(tmp.Path, "Coop2D.exe", null));
        Assert.Contains("No se encuentra Coop2D.exe", ex.Message);
        Assert.ThrowsAny<ArgumentException>(() => manager.LaunchAdditionalInstance("", FakeExe, null));
        Assert.ThrowsAny<ArgumentException>(() => manager.LaunchAdditionalInstance(tmp.Path, " ", null));
    }

    [Fact]
    public async Task LaunchAdditionalInstance_WhileGameIsRunning_StartsAnUntrackedProcess()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        try
        {
            using var manager = new GameProcessManager();
            var started = 0;
            var exitedEvents = 0;
            manager.GameStarted += (_, _) => Interlocked.Increment(ref started);
            manager.GameExited += (_, _) => Interlocked.Increment(ref exitedEvents);

            manager.Launch(gameDir, FakeExe, "/c ping -n 60 127.0.0.1 >nul");
            var trackedField = typeof(GameProcessManager)
                .GetField("_process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var trackedBefore = trackedField.GetValue(manager);

            // Aunque el juego ya esté abierto, se abre otra instancia (Launch lanzaría «El juego ya está abierto.»).
            var pid = manager.LaunchAdditionalInstance(gameDir, FakeExe, "/c ping -n 60 127.0.0.1 >nul");

            Assert.True(pid > 0);
            Assert.NotEqual(Environment.ProcessId, pid);
            var path = GameProcessManager.TryGetProcessImagePath(pid);
            Assert.NotNull(path);
            Assert.Equal(Path.Combine(gameDir, FakeExe), path, ignoreCase: true);

            // No cambia lo que se sigue ni dispara eventos.
            await Task.Delay(200);
            Assert.Same(trackedBefore, trackedField.GetValue(manager));
            Assert.True(manager.IsRunning);
            Assert.Equal(1, Volatile.Read(ref started));
            Assert.Equal(0, Volatile.Read(ref exitedEvents));

            // Kill cierra todas las instancias que se ejecutan desde la carpeta del juego (si no, lanzaría).
            manager.Kill(gameDir, FakeExe);
            Assert.False(GameProcessManager.IsRunningFrom(gameDir, FakeExe));
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public async Task LaunchAdditionalInstance_ExitOfExtraInstance_DoesNotRaiseGameExited()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        try
        {
            using var manager = new GameProcessManager();
            var exitedEvents = 0;
            manager.GameExited += (_, _) => Interlocked.Increment(ref exitedEvents);

            // Sin juego seguido: la instancia extra tampoco pasa a seguirse.
            var pid = manager.LaunchAdditionalInstance(gameDir, FakeExe, "/c exit 4");
            Assert.True(pid > 0);
            Assert.False(manager.IsRunning);

            await WaitUntilAsync(() => !GameProcessManager.IsRunningFrom(gameDir, FakeExe), TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            Assert.Equal(0, Volatile.Read(ref exitedEvents));
            Assert.False(manager.IsRunning);
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }

    [Fact]
    public void LaunchAdditionalInstance_UsesGameFolderAsWorkingDirectory()
    {
        using var tmp = new TempDir();
        var gameDir = CreateFakeGame(tmp);
        var marker = Path.Combine(gameDir, "cwd.txt");
        try
        {
            using var manager = new GameProcessManager();

            // cmd escribe su carpeta de trabajo en cwd.txt (ruta relativa: solo sale bien si WorkingDirectory = gameDir).
            var pid = manager.LaunchAdditionalInstance(gameDir, FakeExe, "/c cd > cwd.txt");
            Assert.True(pid > 0);

            var sw = Stopwatch.StartNew();
            string? content = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                try
                {
                    if (File.Exists(marker))
                    {
                        content = File.ReadAllText(marker).Trim();
                        if (content.Length > 0)
                            break;
                    }
                }
                catch (IOException)
                {
                    // cmd aún lo tiene abierto
                }

                Thread.Sleep(50);
            }

            Assert.Equal(gameDir.TrimEnd('\\'), content, ignoreCase: true);
        }
        finally
        {
            KillLeftovers(tmp.Path);
        }
    }
}
