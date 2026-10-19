using System.Windows;
using PalitoLauncher.Core.Logging;

namespace PalitoLauncher.Services;

/// <summary>
/// Portapapeles con reintentos: Windows solo deja abrirlo a una aplicación a la vez, así que copiar puede fallar si otra
/// (un gestor de portapapeles, una llamada de Discord…) lo tiene abierto en ese momento. Usar desde el hilo de UI (STA).
/// </summary>
internal static class ClipboardService
{
    private const int Attempts = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>Copia el texto; entre intentos espera sin bloquear la ventana. False si no se pudo. Nunca lanza.</summary>
    public static async Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                if (attempt == Attempts)
                {
                    Log.Warn("No se pudo copiar al portapapeles (otra aplicación lo está usando).", ex);
                    return false;
                }
            }
            await Task.Delay(RetryDelay);
        }
        return false;
    }
}
