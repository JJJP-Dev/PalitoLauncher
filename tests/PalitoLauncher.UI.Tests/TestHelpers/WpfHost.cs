using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using PalitoLauncher.Core;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>
/// Hilo STA único con su Dispatcher y una Application con el Themes/Theme.xaml real, compartido por todos los tests del
/// ensamblado (WPF solo admite una Application por proceso). No se muestra ninguna ventana.
/// <para>
/// OJO: NO se puede usar la <see cref="App"/> real. El constructor de Application programa OnStartup para cuando arranca
/// el Dispatcher, y App.OnStartup arranca el launcher de verdad (instancia única, ventana visible, consultas a GitHub…).
/// App.xaml solo fusiona Theme.xaml: si algún día añade más recursos, hay que añadirlos también aquí.
/// </para>
/// <para>
/// Antes de crear nada: los datos del launcher (AppPaths) van a una carpeta temporal (el MainViewModel crea la caché de
/// GitHub en su constructor y no debe tocar %LOCALAPPDATA%) y todo HTTP pasa por un proxy local inexistente, así que una
/// petición accidental falla al momento en vez de gastar el cupo de GitHub.
/// </para>
/// </summary>
internal static class WpfHost
{
    // Como texto: el esquema pack:// no existe hasta que se crea la Application.
    private const string ThemeUri = "pack://application:,,,/PalitoLauncher;component/Themes/Theme.xaml";

    /// <summary>Proxy al que va cualquier petición HTTP de los tests (puerto 9 "discard": nadie escucha).</summary>
    public static readonly Uri BlackholeProxy = new("http://127.0.0.1:9");

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "palito-ui-tests");
    private static readonly Lazy<Dispatcher> LazyDispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentQueue<Exception> Unhandled = new();

    /// <summary>Carpeta temporal que sustituye a %LOCALAPPDATA%\PalitoLauncher durante los tests.</summary>
    public static string AppDataRoot { get; } = Path.Combine(TempRoot, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    public static Dispatcher Dispatcher => LazyDispatcher.Value;

    /// <summary>Excepciones que llegaron sin capturar al Dispatcher (p.ej. desde un async void).</summary>
    public static IReadOnlyCollection<Exception> UnhandledExceptions => Unhandled;

    /// <summary>Ejecuta <paramref name="action"/> en el hilo de UI y espera (las excepciones se propagan al test).</summary>
    public static void Run(Action action) => Dispatcher.Invoke(action);

    public static T Run<T>(Func<T> func) => Dispatcher.Invoke(func);

    /// <summary>
    /// Ejecuta código asíncrono en el hilo de UI: las continuaciones de los await vuelven a ese hilo (como en la app) y
    /// mientras se espera el Dispatcher sigue procesando temporizadores y trabajo pendiente.
    /// </summary>
    public static Task RunAsync(Func<Task> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    public static Task<T> RunAsync<T>(Func<Task<T>> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();

    /// <summary>Desde el hilo de UI: espera (cediendo el Dispatcher) hasta que se cumpla la condición o falla.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Tiempo agotado esperando: {what}");
            await Task.Delay(10);
        }
    }

    /// <summary>Desde el hilo de UI: deja que el Dispatcher vacíe todo lo pendiente (enlaces diferidos, layout, etc.).</summary>
    public static async Task FlushAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static Dispatcher Start()
    {
        DeleteFoldersOfFinishedRuns();
        Directory.CreateDirectory(AppDataRoot);
        AppPaths.OverrideRootForTests(AppDataRoot);
        HttpClient.DefaultProxy = new WebProxy(BlackholeProxy);
        // Nunca notificaciones de Windows reales (aviso de las sesiones de prueba) en el escritorio de quien ejecuta los tests.
        PalitoLauncher.Services.WindowsNotification.SuppressForTests = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(AppDataRoot);

        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += (_, e) =>
                {
                    Unhandled.Enqueue(e.Exception);
                    e.Handled = true; // el hilo de UI debe sobrevivir para el resto de tests
                };

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ThemeUri, UriKind.Absolute) });
                ready.SetResult(dispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WpfHost (STA)",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>ProcessExit no siempre llega (el runner puede matar el proceso): se borran las carpetas de procesos ya terminados.</summary>
    private static void DeleteFoldersOfFinishedRuns()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                if (pid == Environment.ProcessId || IsRunning(pid)) continue;
                TryDelete(dir);
            }
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // best effort
        }
    }
}
