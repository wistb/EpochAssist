using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The named colours features draw rarity, danger and zone kinds with (contracts/colour-roles.md): item rarity, enemy
/// rarity, danger and the session's zone kinds. Their sources are the theme's (preset defaults, theme file values, the Colour vision palettes);
/// the UI pushes the resolved set here, so Core features read them without naming a UI type. The first read in a frame
/// calls Sync, which pushes a new set only when the theme, palette or theme files changed. No feature keeps rarity or
/// danger colours of its own.
/// </summary>
internal static class ColourRoles
{
    /// <summary>Role names, the theme files' keys (permanent), in this order.</summary>
    public static readonly string[] Names =
    {
        "rarity.item.normal", "rarity.item.magic", "rarity.item.rare", "rarity.item.exalted", "rarity.item.unique", "rarity.item.set", "rarity.item.legendary",
        "rarity.enemy.normal", "rarity.enemy.magic", "rarity.enemy.rare", "rarity.enemy.boss",
        "danger.low", "danger.medium", "danger.high",
        "zone.town", "zone.echo", "zone.dungeon", "zone.other",
        "stat.amber", "stat.corruption", "stat.gold", "stat.xp",
    };

    public const int ItemFirst = 0, EnemyFirst = 7, DangerFirst = 11, ZoneFirst = 14, StatFirst = 18, Count = 22;

    /// <summary>
    /// The defaults every preset shares: the game's own colours (game facts GF-65). Items as its item filter colours
    /// them; magic and rare enemies as their glow; normal enemies white and bosses red, which the game doesn't colour;
    /// danger yellow, orange and red; zone kinds town muted grey, echo violet, dungeon orange, other teal. Each rarity
    /// and danger group is at least ΔE00 25 apart, the zone kinds 19 (dev `theme check`). Session figures as the game
    /// draws them (game facts GF-105): memory amber its crystal's yellow, corruption the monolith's purple spiral, gold
    /// and XP their icons' golds; alike as in the game, so not checked apart.
    /// </summary>
    public static readonly Color[] Defaults =
    {
        Hex(0xFFFFFF), Hex(0x36A3E2), Hex(0xE3D157), Hex(0xC184FF), Hex(0xAD5614), Hex(0x52A558), Hex(0xFB2873),
        Hex(0xFFFFFF), Hex(0x3C7AFF), Hex(0xFFA42C), Hex(0xFF3030),
        Hex(0xFFD84A), Hex(0xFF8C1A), Hex(0xFF3030),
        Hex(0x6E7681), Hex(0xA371F7), Hex(0xE3873A), Hex(0x3FB6A8),
        Hex(0xD8D343), Hex(0xA456D1), Hex(0xC3AE6A), Hex(0xD4C988),
    };

    // Item rarity names, as ItemDropped.RarityName gives them (any case).
    private static readonly string[] s_itemNames = { "normal", "magic", "rare", "exalted", "unique", "set", "legendary" };

    private static readonly Color[] s_values = (Color[])Defaults.Clone();
    private static readonly HashSet<string> s_unknown = new();
    private static int s_syncedFrame = -1;

    /// <summary>Raised whenever the resolved set changes, so a feature can cache colours by it.</summary>
    public static int Version { get; private set; }

    /// <summary>Set by the UI at load: brings the roles up to date with the theme and palette (Theme.SyncRoles).</summary>
    internal static Action? Sync;

    /// <summary>
    /// The game's item rarity number: 0 normal, 1–2 magic, 3–4 rare (affix counts), 5–6 exalted, 7 unique, 8 set, 9
    /// legendary; anything else reads as normal. ItemNamed is the surer read for a drop (the game's own checks).
    /// </summary>
    public static Color Item(int rarity) => Read(ItemFirst + rarity switch
    {
        1 or 2 => 1,
        3 or 4 => 2,
        5 or 6 => 3,
        7 => 4,
        8 => 5,
        9 => 6,
        _ => 0,
    });

    /// <summary>By rarity name (normal … legendary, as ItemDropped.RarityName); an unknown name reads as normal.</summary>
    public static Color ItemNamed(string name)
    {
        for (int i = 0; i < s_itemNames.Length; i++)
        {
            if (string.Equals(s_itemNames[i], name, StringComparison.OrdinalIgnoreCase)) return Read(ItemFirst + i);
        }
        return Read(ItemFirst);
    }

    public static Color Enemy(Rarity rarity) => Read(EnemyFirst + Math.Clamp((int)rarity, 0, 3));

    /// <summary>0 low, 1 medium, 2 high (clamped).</summary>
    public static Color Danger(int level) => Read(DangerFirst + Math.Clamp(level, 0, 2));

    // Zone kinds, as session zone records name them.
    private static readonly string[] s_zoneNames = { "town", "echo", "dungeon", "other" };

    /// <summary>By zone kind (town, echo, dungeon, other); an unknown kind reads as other.</summary>
    public static Color Zone(string kind)
    {
        int index = Array.IndexOf(s_zoneNames, kind);
        return Read(ZoneFirst + (index >= 0 ? index : 3));
    }

    // Session figures, as stat roles name them.
    private static readonly string[] s_statNames = { "amber", "corruption", "gold", "xp" };

    /// <summary>A session figure's colour (amber, corruption, gold, xp); an unknown name reads as xp.</summary>
    public static Color Stat(string name)
    {
        int index = Array.IndexOf(s_statNames, name);
        return Read(StatFirst + (index >= 0 ? index : 3));
    }

    /// <summary>Any role by name; magenta for an unknown name, logged once.</summary>
    public static Color Get(string name)
    {
        int index = Array.IndexOf(Names, name);
        if (index >= 0) return Read(index);
        if (s_unknown.Add(name)) Log.Warn($"Colour role '{name}' doesn't exist; drawn magenta");
        return Color.magenta;
    }

    /// <summary>The UI's resolved set, in Names order; Version goes up only when a colour changed.</summary>
    internal static void Push(Color[] values)
    {
        if (values.Length != Count) return;
        bool changed = false;
        for (int i = 0; i < Count; i++)
        {
            if (s_values[i] == values[i]) continue;
            s_values[i] = values[i];
            changed = true;
        }
        if (changed) Version++;
    }

    private static Color Read(int index)
    {
        int frame = Time.frameCount;
        if (frame != s_syncedFrame)
        {
            s_syncedFrame = frame;
            try
            {
                Sync?.Invoke();
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("roles:sync", $"Colour roles couldn't follow the theme: {e.Message}", 30);
            }
        }
        return s_values[index];
    }

    private static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
