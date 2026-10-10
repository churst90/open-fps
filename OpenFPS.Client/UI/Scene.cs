using System.Drawing.Drawing2D;

namespace OpenFPS.Client.UI;

/// <summary>
/// The backdrop of the main menu and the game window, as the Linux head has it: a dark blue gradient
/// with light text, standing in for the picture of the world until the game draws one. Styling only.
/// </summary>
internal static class Scene
{
    private static readonly Color Top = Color.FromArgb(0x0b, 0x1d, 0x3a);
    private static readonly Color Bottom = Color.FromArgb(0x2f, 0x66, 0x90);
    public static readonly Color Text = Color.FromArgb(0xf2, 0xf5, 0xf8);

    /// <summary>Paints the gradient over a form's client area; call from OnPaintBackground.</summary>
    public static void Paint(Graphics g, Rectangle area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        using var brush = new LinearGradientBrush(area, Top, Bottom, 70f);
        g.FillRectangle(brush, area);
    }

    /// <summary>Labels and panels on the backdrop: see-through, light text. Buttons keep their own look.</summary>
    public static void Style(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label or Panel)
            {
                c.BackColor = Color.Transparent;
                c.ForeColor = Text;
            }
            Style(c);
        }
    }
}
