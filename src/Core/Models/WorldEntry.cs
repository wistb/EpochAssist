using UnityEngine;

namespace EpochAssist.Core;

internal enum WorldKind { Enemy, Player, Shrine, Chest }

/// <summary>The game's display classes for enemies (DisplayActorClass), lowest first.</summary>
internal enum Rarity { Normal, Magic, Rare, Boss }

/// <summary>
/// One object the client knows in the current scene. Values are as of RefreshedAt; a reader that keeps an entry across
/// frames checks Gone (and Dead) before each use. Other players carry no name or other identity.
/// </summary>
internal sealed class WorldEntry
{
    public WorldKind Kind { get; internal set; }
    /// <summary>Enemies, shrines and chests only; never set for other players.</summary>
    public string? Name { get; internal set; }
    public Vector3 Position { get; internal set; }
    /// <summary>Straight-line metres from the player at the last refresh.</summary>
    public float Distance { get; internal set; }
    public bool Active { get; internal set; }
    /// <summary>The frame of the last refresh.</summary>
    public int RefreshedAt { get; internal set; }
    /// <summary>Unscaled time of the last refresh.</summary>
    public float RefreshedTime { get; internal set; }
    /// <summary>Active or within the near radius at the last refresh, so refreshed every frame.</summary>
    public bool Hot { get; internal set; }
    public Rarity? Rarity { get; internal set; }
    public IReadOnlyList<DataEntry> Mods { get; internal set; } = Array.Empty<DataEntry>();
    public float? Health { get; internal set; }
    public float? MaxHealth { get; internal set; }
    public float? HealthFraction { get; internal set; }
    public float? Ward { get; internal set; }
    public bool Dead { get; internal set; }
    /// <summary>An echo objective; null where the mode doesn't know (online).</summary>
    public bool? Objective { get; internal set; }
    /// <summary>Shrines and chests: used or opened; null where the mode doesn't say.</summary>
    public bool? Used { get; internal set; }
    /// <summary>The object is gone (destroyed, removed at discovery, or from an earlier scene).</summary>
    public bool Gone { get; internal set; }

    /// <summary>Seconds since the last refresh.</summary>
    public float Age => Time.unscaledTime - RefreshedTime;

    // The game objects behind the entry, for the model's own refresh only.
    internal IntPtr Key;
    internal Il2Cpp.ActorVisuals? Visuals;
    internal Il2Cpp.Actor? Actor;
    internal Il2Cpp.ActorSync? Sync;
    internal Il2Cpp.ShrineSync? ShrineSync;
    internal GameObject? Object;
    internal Il2Cpp.ConditionHandler? Handler;
    // Kept from discovery: each getter call makes a new wrapper, and the hot refresh runs every frame.
    internal Transform? Transform;
    internal Il2Cpp.BaseHealth? HealthSource;
    internal Il2Cpp.ProtectionClass? WardSource;
    internal int SeenStamp;

    internal void Drop()
    {
        Gone = true;
        Visuals = null;
        Actor = null;
        Sync = null;
        ShrineSync = null;
        Object = null;
        Handler = null;
        Transform = null;
        HealthSource = null;
        WardSource = null;
    }
}

/// <summary>A filter for the world queries. Alive defaults to true for enemies and players.</summary>
internal readonly struct WorldQuery
{
    public WorldKind? Kind { get; init; }
    public bool? Alive { get; init; }
    /// <summary>Only active entries.</summary>
    public bool Active { get; init; }
    public Rarity MinRarity { get; init; }
    public bool? Objective { get; init; }
    public bool? Used { get; init; }
    /// <summary>Metres from the player; null for the whole zone.</summary>
    public float? Range { get; init; }
}

/// <summary>What keeping the world current cost over the last second, and how old the entries are.</summary>
internal readonly record struct WorldCost(double AverageMs, double WorstMs, int Overruns, int Hot, int Cold, int Stale)
{
    /// <summary>The last discovery's time, the slowest frame since start, and whether a collection ran in it.</summary>
    public double DiscoveryMs { get; init; }
    public double SlowestMs { get; init; }
    public string SlowestCause { get; init; } = string.Empty;
}
