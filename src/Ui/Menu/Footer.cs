using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// The 34 px bar across the bottom: a fixed set of quick toggles (the HUD panel's are the player's choice), then the quiet
// About, Feedback and Report a bug links and, past a divider, the close hint.
internal static class Footer
{
    public const float Height = 34f;

    // By action id, in order; one that isn't loaded is left out.
    private static readonly string[] s_switches =
    {
        "setting.toggle.macros.enabled", "setting.toggle.planner.enabled", "setting.toggle.interact.enabled", "setting.toggle.mapreveal.enabled",
    };

    private static readonly (string Label, string Key, string Tip, Action Run)[] s_links =
    {
        ("About", "about", "Version, links and disclaimers", () => Menu.Open(MenuRoutes.About)),
        ("Feedback", "feedback", "Opens the mod's Nexus posts in the browser", AboutPage.Feedback),
        ("Report a bug", "bug", "Copies diagnostics and opens a new GitHub issue in the browser", AboutPage.ReportBug),
    };

    public static void Draw(Rect bar)
    {
        Focus.Scope = "foot";
        if (Widgets.Themed) Theme.Fill(new Rect(bar.x, bar.y, bar.width, 1f), Theme.DividerColor);
        GUIStyle label = Widgets.Style(() => Theme.FooterLabel, GUI.skin.label);
        GUIStyle hintStyle = Widgets.Style(() => Theme.FooterHint, GUI.skin.label);
        string hint = $"{KeySettings.MenuKey.Display} or Esc to close";
        float hintWidth = TextCache.Width(hint, hintStyle);
        // The links end 14 px before a divider, which sits 14 px before the hint.
        float linksEnd = bar.xMax - hintWidth - 12f - 29f;
        float linksStart = Links(bar, linksEnd);
        if (linksStart < linksEnd && Widgets.Themed)
            Theme.Fill(new Rect(linksEnd + 14f, Mathf.Round(bar.center.y - 7f), 1f, 14f), Theme.DividerColor);
        float x = bar.x + 12f, limit = linksStart - 16f;
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

    // Right to left from the hint; returns where the first link starts.
    private static float Links(Rect bar, float right)
    {
        GUIStyle link = Widgets.Style(() => Theme.FooterLink, GUI.skin.label), lit = Widgets.Style(() => Theme.FooterLinkLit, GUI.skin.label);
        float width = s_links.Sum(l => TextCache.Width(l.Label, link)) + 14f * (s_links.Length - 1);
        // A window too narrow for them and a switch leaves them out.
        if (right - width < bar.x + 12f + 120f) return right;
        float x = right - width;
        foreach (var (text, key, tip, run) in s_links)
        {
            float w = TextCache.Width(text, link);
            var r = new Rect(x, bar.y + 5f, w, bar.height - 10f);
            bool clicked = GUI.Button(r, GUIContent.none, GUIStyle.none);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(x, bar.y, w, bar.height), text, r.Contains(Event.current.mousePosition) ? lit : link);
            Popups.Tip(r, tip, "link." + key);
            if (Focus.Item("link." + key, r) || clicked) run();
            x += w + 14f;
        }
        return right - width;
    }
}
