using System.Windows.Input;

namespace PalitoLauncher.ViewModels;

/// <summary>Enlace de comunidad de launcher.json (Discord, itch.io, Steam…): botón en Inicio y entrada en el menú ⋯.</summary>
public sealed class CommunityLinkViewModel : ObservableObject
{
    // Glifos de Segoe Fluent Icons / Segoe MDL2 Assets. Discord no tiene glifo propio: se usa el de «mensaje».
    internal const string GlyphChat = "";
    internal const string GlyphGlobe = "";
    internal const string GlyphGame = "";
    internal const string GlyphVideo = "";
    internal const string GlyphCode = "";

    public CommunityLinkViewModel(string label, string? icon, string url, Action<string> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        Label = (label ?? "").Trim();
        Url = (url ?? "").Trim();
        Glyph = GlyphFor(icon);
        ToolTip = Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? $"Abrir {uri.Host}" : Label;
        OpenCommand = new RelayCommand(() => open(Url), () => IsHttps(Url));
    }

    public string Label { get; }
    public string Url { get; }
    public string Glyph { get; }

    /// <summary>"Abrir discord.gg".</summary>
    public string ToolTip { get; }

    public ICommand OpenCommand { get; }

    /// <summary>Icono de launcher.json ("discord", "itch", "steam", "youtube", "github", "web") → glifo.</summary>
    internal static string GlyphFor(string? icon) => (icon ?? "").Trim().ToLowerInvariant() switch
    {
        "discord" => GlyphChat,
        "steam" => GlyphGame,
        "youtube" => GlyphVideo,
        "github" => GlyphCode,
        _ => GlyphGlobe, // "itch", "web" y cualquier otro
    };

    /// <summary>Solo se abren enlaces https absolutos.</summary>
    internal static bool IsHttps(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
