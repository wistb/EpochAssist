using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The 34 px bar across the bottom: a fixed set of quick toggles (the HUD panel's are the player's choice), then the close
// hint.
internal static class Footer
{
    public const float Height = 34f;

    // By action id, in order; one that isn't loaded is left out.
    private static readonly string[] s_switches =
    {
        "setting.toggle.macros.enabled", "setting.toggle.planner.enabled", "setting.toggle.interact.enabled", "setting.toggle.mapreveal.enabled",
    };

    public static void Draw(Rect bar)
    {
        Focus.Scope = "foot";
        if (Widgets.Themed) Theme.Fill(new Rect(bar.x, bar.y, bar.width, 1f), Theme.DividerColor);
        GUIStyle label = Widgets.Style(() => Theme.FooterLabel, GUI.skin.label);
        GUIStyle hintStyle = Widgets.Style(() => Theme.FooterHint, GUI.skin.label);
        string hint = $"{KeySettings.MenuKey.Display} or Esc closes";
        float hintWidth = TextCache.Width(hint, hintStyle);
        float x = bar.x + 12f, limit = bar.xMax - hintWidth - 24f;
        var quick = SettingsRegistry.QuickToggles.ToList();
        foreach (string id in s_switches)
        {
            if (quick.Find(q => q.ActionId == id) is not { } toggle) continue;
            string text = toggle.Label;
            float w = Controls.SwitchWidth + 6f + TextCache.Width(text, label);
            if (x + w > limit) break;
            var r = new Rect(x, bar.y + 5f, w, bar.height - 10f);
            // The whole item flips, label and switch.
            if (Controls.Switch(r, toggle.Value, toggle.ActionId, toggle.Tip) != toggle.Value) SettingRows.Flip(toggle, "menu footer");
            if (Event.current.type == EventType.Repaint)
                Widgets.Paint(new Rect(x + Controls.SwitchWidth + 6f, r.y, w - Controls.SwitchWidth - 6f, r.height), text, label);
            x += w + 16f;
        }
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(bar.xMax - hintWidth - 12f, bar.y, hintWidth, bar.height), hint, hintStyle);
        Focus.Scope = string.Empty;
    }
}
