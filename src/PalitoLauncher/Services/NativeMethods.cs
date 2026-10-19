using System.Runtime.InteropServices;
using System.Text;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Services;

/// <summary>P/Invoke de Win32 que necesita la UI (parpadeo en la barra de tareas, primer plano, ruta de procesos).</summary>
internal static class NativeMethods
{
    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;
    private const int ASFW_ANY = -1;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    // FOLDERID_Downloads (la carpeta Descargas real, aunque el usuario la haya movido a otra unidad).
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
        out IntPtr ppszPath);

    /// <summary>Carpeta Descargas del usuario (null si no se puede saber). Nunca lanza.</summary>
    public static string? TryGetDownloadsFolder()
    {
        var pointer = IntPtr.Zero;
        try
        {
            int hr = SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out pointer);
            return hr == 0 && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo leer la carpeta Descargas.", ex);
            return null;
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }

    /// <summary>Hace parpadear el botón de la barra de tareas hasta que la ventana pase a primer plano. Nunca lanza.</summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                uCount = uint.MaxValue,
                dwTimeout = 0,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo hacer parpadear la ventana.", ex);
        }
    }

    /// <summary>Detiene el parpadeo. Nunca lanza.</summary>
    public static void StopFlashing(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_STOP,
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo detener el parpadeo de la ventana.", ex);
        }
    }

    /// <summary>Permite que otro proceso (la instancia principal) pase a primer plano. Nunca lanza.</summary>
    public static void AllowAnyProcessToSetForeground()
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);
        }
        catch (Exception ex)
        {
            Log.Warn("AllowSetForegroundWindow falló.", ex);
        }
    }

    /// <summary>Ruta completa del ejecutable de un proceso (null si no se puede leer). Nunca lanza.</summary>
    public static string? TryGetProcessImagePath(int processId)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero) return null;
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer la ruta del proceso {processId}.", ex);
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }
}
