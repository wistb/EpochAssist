using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Features;

internal static partial class AutoCast
{
    // Toggled slots pulse while autocast may cast (the enemy gate holding it counts); they turn blue while paused
    // or while the hold-to-pause key is held.
    private static void UpdateIcons(Run run)
    {
        bool[] toggles = CurrentToggles();
        bool anyToggled = false;
        for (int i = 0; i < SlotCount; i++) anyToggled |= toggles[i];

        if (!anyToggled)
        {
            if (run.IconsTinted) RestoreIcons(run);
            return;
        }

        var icons = Game.BarIcons;
        if (icons == null) return;

        PauseRule? rule = run.Client?.Blocking;
        bool held = rule != null && (rule.Kind == RuleKind.Latched || rule.Name == "hold-key");

        float pulse = (Mathf.Sin(Time.unscaledTime * 5f) + 1f) * 0.5f;
        float scale = Mathf.Lerp(0.92f, 1.07f, pulse);
        var activeColor = new Color(1f, Mathf.Lerp(1f, 0.75f, pulse), Mathf.Lerp(1f, 0.4f, pulse), 1f);
        var suspendedColor = new Color(0.55f, 0.75f, 1f, Mathf.Lerp(0.55f, 0.85f, pulse));

        for (int i = 0; i < icons.Count; i++)
        {
            AbilityBarIcon? barIcon = icons[i];
            if (barIcon == null) continue;

            int slot = barIcon.abilityNumber - 1;
            bool toggled = slot >= 0 && slot < SlotCount && toggles[slot];
            bool on = toggled && !held;
            bool paused = toggled && held;

            barIcon.transform.localScale = on ? new Vector3(scale, scale, 1f) : Vector3.one;

            var image = barIcon.icon;
            if (image != null)
            {
                image.color = on ? activeColor : paused ? suspendedColor : Color.white;
            }
        }

        run.IconsTinted = true;
    }

    private static void RestoreIcons(Run run)
    {
        run.IconsTinted = false;

        var icons = Game.BarIcons;
        if (icons == null) return;

        for (int i = 0; i < icons.Count; i++)
        {
            AbilityBarIcon? barIcon = icons[i];
            if (barIcon == null) continue;

            barIcon.transform.localScale = Vector3.one;
            var image = barIcon.icon;
            if (image != null) image.color = Color.white;
        }
    }
}
