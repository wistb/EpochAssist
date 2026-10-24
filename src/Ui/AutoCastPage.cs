using EpochAssist.Core;
using EpochAssist.Features;
using UnityEngine;
using static EpochAssist.Ui.Widgets;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Ui;

// Autocast's menu page below the header: slot chips, the toggle hint, and its settings as cards on one scrolling page.
internal static class AutoCastPage
{
    /// <summary>The dispatcher's status while autocast runs, otherwise off with the reason.</summary>
    private static FeatureStatus Status => FeatureHost.Get<AutoCastFeature>()?.Status ?? FeatureStatus.Off(string.Empty);

    public static void Draw()
    {
        FeatureStatus status = Status;
        SlotChips(52f);
        Hint($"Hold {AutoCast.ModifierKeyName} and press a skill's key to toggle its slot."
            + (status.Detail.Length > 0 ? $"  {Colored(status.Detail, Theme.StateColor(status.State))}" : string.Empty));
        GUILayout.Space(8f);

        bool wasEnabled = GUI.enabled;
        Color previousColor = GUI.color;
        GUI.enabled = S.Enabled.Value;
        if (!GUI.enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);

        Menu.BeginScroll();
        foreach (var section in S.PageSections) SettingsView.Card(section, extra: section == S.Gate ? GateReadout : null);
        Menu.EndScroll();

        GUI.color = previousColor;
        GUI.enabled = wasEnabled;
    }

    // What the enemy gate sees right now, while it is on.
    private static void GateReadout()
    {
        if (!S.RequireEnemies.Value) return;
        string gate = AutoCast.EnemyGateOpen ? Colored("casting", Theme.Good) : Colored("holding", Theme.Pause);
        Hint($"In range: {AutoCast.EnemiesInRange} enemies, weight {AutoCast.EnemyWeightInRange:F0}  ·  {gate}");
    }

    /// <summary>Five slot chips across the available width.</summary>
    private static void SlotChips(float height)
    {
        Rect row = GUILayoutUtility.GetRect(10f, height, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;

        FeatureStatus status = Status;
        const float gap = 6f;
        float width = (row.width - gap * (AutoCast.Slots - 1)) / AutoCast.Slots;
        for (int i = 0; i < AutoCast.Slots; i++)
        {
            var chip = new Rect(row.x + i * (width + gap), row.y, width, row.height);
            bool toggled = status.Slots?[i] ?? false;
            bool live = toggled && status.State == StatusState.Active;
            string name = AutoCast.SlotAbilityName(i) ?? "-";

            if (!Themed)
            {
                GUI.Box(chip, $"{i + 1}\n{(toggled ? "ON" : "off")}");
                continue;
            }
            Theme.DrawSliced(chip, live ? Theme.ChipOn : toggled ? Theme.ChipPaused : Theme.ChipOff, Theme.ChipRadius);
            Theme.ChipNumber.normal.textColor = live ? Theme.AccentText : toggled ? Theme.Pause : Theme.Dim;
            Theme.ChipName.normal.textColor = toggled ? Theme.Text : Theme.Dim;
            GUI.Label(new Rect(chip.x, chip.y + 6f, chip.width, 22f), (i + 1).ToString(), Theme.ChipNumber);
            GUI.Label(new Rect(chip.x + 3f, chip.y + 28f, chip.width - 6f, 16f), Menu.Fit(name, Theme.ChipName, chip.width - 8f), Theme.ChipName);
        }
    }
}
