using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The orange "Mod conflict: <mods>" pill in the header of a page whose feature another mod conflicts with (by the mod
// overlap check: known mods that may affect it, and other patches on methods it patches). Clicking opens the Mods page.
internal static class ConflictTag
{
    private const string ModsPage = "combat/general/diagnostics/mods";

    private static IReadOnlyList<(string Mod, string Why)>? s_for;
    private static string s_text = string.Empty, s_tip = string.Empty;

    /// <summary>The pill's width for a feature's conflicts, 0 with none.</summary>
    public static float Width(Feature? feature) => Words(feature) is { } words ? Controls.ConflictPillWidth(words.Text) : 0f;

    /// <summary>Draws the pill at x, centred on cy, when the feature has conflicts; returns its width (0 with none).</summary>
    public static float Draw(Feature? feature, float x, float cy, string key)
    {
        if (Words(feature) is not { } words) return 0f;
        float w = Controls.ConflictPillWidth(words.Text);
        if (Controls.ConflictPill(new Rect(x, Mathf.Round(cy - Theme.TagHeight / 2f), w, Theme.TagHeight), words.Text, words.Tip, key) && MenuRoutes.Resolve(ModsPage) is { } mods) MenuRoutes.Show(mods);
        return w;
    }

    // The pill's text and tip, made once per conflict list.
    private static (string Text, string Tip)? Words(Feature? feature)
    {
        var conflicts = ModOverlap.ConflictsFor(feature?.Id);
        if (conflicts.Count == 0) return null;
        if (!ReferenceEquals(conflicts, s_for))
        {
            s_for = conflicts;
            s_text = "Mod conflict: " + string.Join(", ", conflicts.Select(c => c.Mod));
            s_tip = string.Join("\n", conflicts.Select(c => $"{c.Mod}: {c.Why}")) + "\nClick for the Mods page.";
        }
        return (s_text, s_tip);
    }
}
