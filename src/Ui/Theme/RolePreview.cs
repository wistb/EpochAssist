using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// The colour roles as the mod's overlays will draw them, under Colour vision on the Appearance page: one line each for
/// item rarity, enemy rarity, danger and zone kinds, a swatch and the name in its colour. Read from ColourRoles, so it shows the
/// theme's values or the chosen palette.
/// </summary>
internal static class RolePreview
{
    public const string Block = "appearance.roles";

    private const float SwatchSize = 10f, SwatchGap = 5f, ItemGap = 14f;

    private static readonly (string Label, (string Name, int Role)[] Roles)[] s_groups =
    {
        ("Items", new[] { ("Normal", 0), ("Magic", 1), ("Rare", 2), ("Exalted", 3), ("Unique", 4), ("Set", 5), ("Legendary", 6) }),
        ("Enemies", new[] { ("Normal", 7), ("Magic", 8), ("Rare", 9), ("Boss", 10) }),
        ("Danger", new[] { ("Low", 11), ("Medium", 12), ("High", 13) }),
        ("Zones", new[] { ("Town", 14), ("Echo", 15), ("Dungeon", 16), ("Other", 17) }),
        ("Session", new[] { ("Amber", 18), ("Corruption", 19), ("Gold", 20), ("XP", 21) }),
    };

    public static void Register() => MenuBlocks.Register(Block, (rect, _) => Draw(rect), _ => BridgeLines(), (_, width) => Height(width));

    private static IEnumerable<string> BridgeLines() =>
        s_groups.Select(g => $"roles {g.Label.ToLowerInvariant()} " + string.Join(" ", g.Roles.Select(r => $"{r.Name.ToLowerInvariant()}={ColorSetting.Format(ColourRoles.Get(ColourRoles.Names[r.Role]))}")));

    // A line per group, wrapping to more when the page is narrow.
    private static float Height(float width) => s_groups.Sum(g => Lines(g.Roles, ItemsWidth(width)).Count) * RowLayout.ControlHeight + RowLayout.RowGap;

    private static void Draw(Rect rect)
    {
        if (Event.current.type != EventType.Repaint) return;
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        float labelWidth = RowLayout.LabelWidth(rect.width), y = rect.y;
        foreach (var (title, roles) in s_groups)
        {
            Widgets.Paint(new Rect(rect.x, y, labelWidth, RowLayout.ControlHeight), title, label);
            foreach (var line in Lines(roles, ItemsWidth(rect.width)))
            {
                float x = rect.x + labelWidth + RowLayout.Gap;
                foreach (var (name, role) in line)
                {
                    Color colour = ColourRoles.Get(ColourRoles.Names[role]);
                    Theme.Fill(new Rect(x, Mathf.Round(y + (RowLayout.ControlHeight - SwatchSize) / 2f), SwatchSize, SwatchSize), colour);
                    x += SwatchSize + SwatchGap;
                    float w = TextCache.Width(name, label) + label.padding.horizontal;
                    Widgets.Paint(new Rect(x, y, w, RowLayout.ControlHeight), Widgets.Themed ? Widgets.Colored(name, colour) : name, label);
                    x += w + ItemGap;
                }
                y += RowLayout.ControlHeight;
            }
        }
    }

    private static float ItemsWidth(float width) => width - RowLayout.LabelWidth(width) - RowLayout.Gap;

    // The roles split into lines that fit the width.
    private static List<List<(string Name, int Role)>> Lines((string Name, int Role)[] roles, float width)
    {
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        var lines = new List<List<(string, int)>> { new() };
        float used = 0f;
        foreach (var role in roles)
        {
            float w = SwatchSize + SwatchGap + TextCache.Width(role.Name, label) + label.padding.horizontal + ItemGap;
            if (used + w > width && lines[^1].Count > 0)
            {
                lines.Add(new List<(string, int)>());
                used = 0f;
            }
            lines[^1].Add(role);
            used += w;
        }
        return lines;
    }
}
