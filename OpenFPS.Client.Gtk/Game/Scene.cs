using Gtk;

namespace OpenFPS.Client.Gtk.Game;

/// <summary>
/// The backdrop of the main menu and the game window: a dark blue gradient with light text, standing in
/// for the picture of the world until the game draws one. Styling only; nothing here is read aloud.
/// </summary>
internal static class Scene
{
    public const string CssClass = "openfps-scene";

    // The lightest stop keeps the text above 6:1 contrast.
    private const string Css = @"
window.openfps-scene { background-image: linear-gradient(160deg, #0b1d3a 0%, #163a63 55%, #2f6690 100%); }
window.openfps-scene label { color: #f2f5f8; }";

    private static bool _installed;

    /// <summary>Loads the style once for the whole display; call on the GTK thread.</summary>
    public static void Install()
    {
        if (_installed || Gdk.Display.GetDefault() is not { } display) return;
        var provider = CssProvider.New();
        provider.LoadFromString(Css);
        StyleContext.AddProviderForDisplay(display, provider, 600);   // GTK_STYLE_PROVIDER_PRIORITY_APPLICATION
        _installed = true;
    }

    /// <summary>Puts the backdrop behind a window.</summary>
    public static void Apply(Window window)
    {
        Install();
        window.AddCssClass(CssClass);
    }
}
