namespace PalitoLauncher.Core.Util;

/// <summary>
/// Error esperado cuyo <see cref="Exception.Message"/> está pensado para mostrarse tal cual al usuario (en español).
/// Cualquier otra excepción se considera un fallo inesperado.
/// </summary>
public class LauncherException : Exception
{
    public LauncherException(string userMessage, Exception? innerException = null)
        : base(userMessage, innerException)
    {
    }

    public string UserMessage => Message;
}
