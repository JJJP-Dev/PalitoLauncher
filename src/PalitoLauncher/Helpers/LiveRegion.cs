using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Helpers;

/// <summary>
/// Avisos para lectores de pantalla. En WPF, AutomationProperties.LiveSetting no hace nada por sí solo: hay que lanzar
/// LiveRegionChanged desde el código cuando el texto cambia.
/// </summary>
public static class LiveRegion
{
    /// <summary>
    /// Anuncia <paramref name="element"/> (con LiveSetting puesto) cuando ya tiene el texto nuevo y es visible (en diferido,
    /// tras los bindings y el layout). Sin lector de pantalla escuchando no hace nada. Nunca lanza.
    /// </summary>
    public static void Announce(UIElement? element)
    {
        if (element == null) return;
        try
        {
            element.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Raise(element)));
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo programar el aviso al lector de pantalla: {ex.Message}");
        }
    }

    private static void Raise(UIElement element)
    {
        try
        {
            if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            var peer = UIElementAutomationPeer.FromElement(element) ?? UIElementAutomationPeer.CreatePeerForElement(element);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception ex)
        {
            Log.Debug($"No se pudo avisar al lector de pantalla: {ex.Message}");
        }
    }
}
