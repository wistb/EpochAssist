using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The 34 px bar across the bottom: the mini HUD's quick toggles, in the same order, then the close hint.
internal static class Footer
{
    public const float Height = 34f;

    public static void Draw(Rect bar)
    {
        Focus.Scope = "foot";
        if (Widgets.Themed) Theme.Fill(new Rect(bar.x, bar.y, bar.width, 1f), Theme.DividerColor);
        GUIStyle label = Widgets.Style(() => Theme.FooterLabel, GUI.skin.label);
        GUIStyle hintStyle = Widgets.Style(() => Theme.FooterHint, GUI.skin.label);
        string hint = $"{KeySettings.MenuKey.Display} or Esc closes";
        float hintWidth = TextCache.Width(hint, hintStyle);
        float x = bar.x + 12f, limit = bar.xMax - hintWidth - 24f;
        foreach (var toggle in SettingsRegistry.QuickToggles)
        {
            string text = toggle.QuickLabel!;
            float w = Controls.SwitchWidth + 6f + TextCache.Width(text, label);
            if (x + w > limit) break;
            var r = new Rect(x, bar.y + 5f, w, bar.height - 10f);
            // The whole item flips, label and switch.
            if (Controls.Switch(r, toggle.Value, SettingsRegistry.ToggleAction(toggle), toggle.Help ?? toggle.Label) != toggle.Value) SettingRows.Flip(toggle, "menu footer");
            if (Event.current.type == EventType.Repaint)
                Widgets.Paint(new Rect(x + Controls.SwitchWidth + 6f, r.y, w - Controls.SwitchWidth - 6f, r.height), text, label);
            x += w + 16f;
        }
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(bar.xMax - hintWidth - 12f, bar.y, hintWidth, bar.height), hint, hintStyle);
        Focus.Scope = string.Empty;
    }
}
