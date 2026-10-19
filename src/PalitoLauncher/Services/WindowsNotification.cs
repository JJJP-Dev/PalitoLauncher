using System.IO;
using System.Windows;
using System.Windows.Threading;
using PalitoLauncher.Core;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Services;

/// <summary>
/// Notificación de Windows (la que aparece abajo a la derecha / en el Centro de notificaciones) con el icono del launcher.
/// Usa un NotifyIcon de WinForms creado solo para el aviso y liberado a los ~10 s: el launcher no se queda en la bandeja
/// del sistema. Usar desde el hilo de UI. Los tipos de WinForms van con su nombre completo (chocan con los de WPF).
/// </summary>
internal static class WindowsNotification
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);
    private static readonly List<System.Windows.Forms.NotifyIcon> Active = new();

    /// <summary>Los tests la activan: nunca deben mostrar notificaciones reales en el escritorio de quien los ejecuta.</summary>
    internal static bool SuppressForTests { get; set; }

    /// <summary>
    /// Muestra la notificación. <paramref name="onClick"/> se ejecuta si el usuario la pulsa (p.ej. traer la ventana al
    /// frente). Devuelve false si no se pudo mostrar. Nunca lanza.
    /// </summary>
    public static bool Show(string title, string message, Action? onClick)
    {
        if (SuppressForTests) return false;

        System.Windows.Forms.NotifyIcon? notifyIcon = null;
        try
        {
            notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = Truncate(LauncherConfig.LauncherName, 63),
                Visible = true,
            };
            if (onClick != null)
            {
                notifyIcon.BalloonTipClicked += (_, _) => RunSafe(onClick);
                notifyIcon.MouseClick += (_, _) => RunSafe(onClick);
            }

            // Windows limita el título a 63 caracteres y el texto a 255.
            notifyIcon.ShowBalloonTip((int)Lifetime.TotalMilliseconds, Truncate(title, 63), Truncate(message, 255),
                System.Windows.Forms.ToolTipIcon.Info);
            Active.Add(notifyIcon);

            var created = notifyIcon;
            var timer = new DispatcherTimer { Interval = Lifetime + TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Release(created);
            };
            timer.Start();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo mostrar la notificación de Windows.", ex);
            if (notifyIcon != null) Release(notifyIcon);
            return false;
        }
    }

    /// <summary>Quita los iconos que queden (al cerrar el launcher), para que no se quede uno «fantasma» en la bandeja.</summary>
    public static void ReleaseAll()
    {
        foreach (var notifyIcon in Active.ToArray()) Release(notifyIcon);
    }

    private static void Release(System.Windows.Forms.NotifyIcon notifyIcon)
    {
        Active.Remove(notifyIcon);
        try
        {
            var icon = notifyIcon.Icon;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            icon?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo liberar el icono de la notificación.", ex);
        }
    }

    /// <summary>Icono de la app (Assets/app.ico como recurso de WPF); si falla, el del ejecutable o el genérico de Windows.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/PalitoLauncher;component/Assets/app.ico", UriKind.Absolute));
            if (resource != null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo cargar el icono de los recursos para la notificación: {ex.Message}");
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } fromExe)
                return fromExe;
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo leer el icono del ejecutable para la notificación: {ex.Message}");
        }

        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private static void RunSafe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Error al responder al clic en la notificación.", ex);
        }
    }

    private static string Truncate(string? text, int max)
    {
        text = (text ?? "").Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
