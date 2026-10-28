namespace EpochAssist.Core;

/// <summary>
/// A short note a feature shows in the mini HUD, beside the pill, for as long as it stays set: what a hovered game element
/// means to the feature. One at a time: the latest note shows, and an owner clears only its own.
/// </summary>
internal static class HudHint
{
    private static Feature? s_owner;

    public static string? Title { get; private set; }
    /// <summary>The note; rich text, so words can take a colour.</summary>
    public static string? Text { get; private set; }
    /// <summary>The note's colour (its stripe and title), or null for the plain card.</summary>
    public static UnityEngine.Color? Accent { get; private set; }
    /// <summary>Shown by the pointer (beside the game's own tooltip when one shows) rather than beside the pill.</summary>
    public static bool AtCursor { get; private set; }

    public static void Show(Feature owner, string title, string text, UnityEngine.Color? accent = null, bool atCursor = false)
    {
        s_owner = owner;
        Title = title;
        Text = text;
        Accent = accent;
        AtCursor = atCursor;
    }

    public static void Clear(Feature owner)
    {
        if (s_owner != owner) return;
        s_owner = null;
        Title = null;
        Text = null;
        Accent = null;
    }
}
