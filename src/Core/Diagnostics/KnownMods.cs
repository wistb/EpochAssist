namespace EpochAssist.Core;

/// <summary>
/// A mod known to overlap Epoch Assist: its names (the MelonInfo name and aliases), what kind of things it does, our
/// feature ids it may affect, and one sentence for the player.
/// </summary>
internal sealed record KnownOverlap(string[] Names, string[] Kinds, string[] Affects, string Text)
{
    public string Name => Names[0];

    /// <summary>"Terrible Tooltips: tooltips; may affect gameui".</summary>
    public string Line => $"{Name}: {string.Join(", ", Kinds)}" + (Affects.Length > 0 ? $"; may affect {string.Join(", ", Affects)}" : string.Empty);
}

/// <summary>
/// The compiled list of mods known to overlap Epoch Assist. Compiled in, so it can't be fetched or tampered with; it
/// grows with releases.
/// </summary>
internal static class KnownMods
{
    public const string Autocaster = "autocaster";

    public static readonly IReadOnlyList<KnownOverlap> All = new[]
    {
        new KnownOverlap(new[] { "LastEpoch_Hud", "LEHud", "Last Epoch HUD" }, new[] { Autocaster, "overlay", "map", "auto pick-up" }, new[] { "macros", "dispatch", "game", "interact", "mapreveal" },
            "Has its own autocast, loot pick-up and minimap reveal, and patches the same skill bar and echo methods; two autocasters press skills twice, both mods may reach for the same item (the game gives it once), and both change the minimap's reveal (the last one to write it wins)."),
        new KnownOverlap(new[] { "Terrible Tooltips", "medick_Terrible_Tooltips" }, new[] { "tooltips" }, new[] { "gameui" },
            "Also changes item tooltips; lines a feature adds to tooltips may sit oddly or twice."),
        new KnownOverlap(new[] { "FallenStar's Auto Enabler", "Fallen_Auto_Enabler" }, new[] { "auto use" }, new[] { "interact" },
            "Uses shrines, chests and rune prisons by itself, from farther away; both may reach for the same object (the game uses it once)."),
    };

    /// <summary>The entry for a mod by its MelonInfo name or its file name (with or without .dll), ignoring case; null for none.</summary>
    public static KnownOverlap? Match(string name, string file)
    {
        string stem = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        foreach (var known in All)
        {
            foreach (string alias in known.Names)
            {
                if (alias.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase) || (stem.Length > 0 && alias.Equals(stem, StringComparison.OrdinalIgnoreCase))) return known;
            }
        }
        return null;
    }
}
