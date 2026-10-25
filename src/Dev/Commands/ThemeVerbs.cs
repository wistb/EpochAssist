using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

// The theme command's dev verbs: set <name> and check (registered by the dev bridge feature).
internal static class ThemeVerbs
{
    private const int SettleFrames = 2;

    public static void Register(string owner)
    {
        Commands.AddDevVerb("theme", "set", owner, call =>
        {
            string name = call.Rest(2);
            if (Themes.Find(name) is not { } theme) return new CommandResult($"error unknown theme {name}") { Settle = SettleFrames };
            Config.Theme.Value = theme.Name;
            return new CommandResult($"ok theme {theme.Name}") { Settle = SettleFrames };
        });
        Commands.AddDevVerb("theme", "check", owner, _ =>
            new CommandResult("ok theme check: " + string.Join(" | ", ThemeValues.Presets.Select(p => ThemeCheck(p.Name, p.Values)))) { Settle = SettleFrames });
    }

    // For one theme: each state colour's lightness (CIE L*), Active's smallest distance to the others (>= 15),
    // each state colour's contrast on the card (>= 3:1, Off >= 1.6:1) and text and dim text on the card (>= 4.5:1).
    // A see-through card is taken over the window colour.
    private static string ThemeCheck(string name, ThemeValues v)
    {
        Color card = Color.Lerp(v.Window, new Color(v.Card.r, v.Card.g, v.Card.b, 1f), v.Card.a);
        var states = new (string Name, Color Color)[]
        {
            ("active", v.StateActive), ("waiting", v.StateWaiting), ("holding", v.StateHolding), ("paused", v.StatePaused), ("off", v.StateOff),
        };
        var failures = new List<string>();
        float active = Lightness(v.StateActive);
        float gap = states.Skip(1).Min(s => Mathf.Abs(Lightness(s.Color) - active));
        if (gap < 15f) failures.Add($"active gap {gap:F0}");
        foreach (var (state, color) in states)
        {
            float needed = state == "off" ? 1.6f : 3f;
            if (Contrast(color, card) < needed) failures.Add($"{state} contrast {Contrast(color, card):F1}");
        }
        if (Contrast(v.Text, card) < 4.5f) failures.Add($"text contrast {Contrast(v.Text, card):F1}");
        if (Contrast(v.Dim, card) < 4.5f) failures.Add($"dim contrast {Contrast(v.Dim, card):F1}");
        string lightness = string.Join(" ", states.Select(s => $"{s.Name} {Lightness(s.Color):F0}"));
        string contrasts = string.Join(" ", states.Select(s => $"{s.Name} {Contrast(s.Color, card):F1}"));
        return $"{name} {(failures.Count == 0 ? "pass" : "fail (" + string.Join(", ", failures) + ")")}: L* {lightness}; active gap {gap:F0}; "
            + $"contrast {contrasts}, text {Contrast(v.Text, card):F1}, dim {Contrast(v.Dim, card):F1}";
    }

    private static float Luminance(Color c)
    {
        static float Linear(float x) => x <= 0.04045f ? x / 12.92f : Mathf.Pow((x + 0.055f) / 1.055f, 2.4f);
        return 0.2126f * Linear(c.r) + 0.7152f * Linear(c.g) + 0.0722f * Linear(c.b);
    }

    private static float Lightness(Color c)
    {
        float y = Luminance(c);
        return y > 216f / 24389f ? 116f * Mathf.Pow(y, 1f / 3f) - 16f : y * 24389f / 27f;
    }

    private static float Contrast(Color a, Color b)
    {
        float la = Luminance(a) + 0.05f, lb = Luminance(b) + 0.05f;
        return Mathf.Max(la, lb) / Mathf.Min(la, lb);
    }
}
