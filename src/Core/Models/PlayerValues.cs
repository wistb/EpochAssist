using Il2Cpp;

namespace EpochAssist.Core;

/// <summary>Health, ward and mana. Ward is null when the current mode has no readable source.</summary>
internal sealed class Vitals
{
    public float Health { get; internal set; }
    public float MaxHealth { get; internal set; }
    public float HealthFraction { get; internal set; }
    public float? Ward { get; internal set; }
    public float Mana { get; internal set; }
    public float MaxMana { get; internal set; }
}

/// <summary>A buff or debuff on the player, one per ailment id, as the buff bar shows it.</summary>
internal sealed class Buff
{
    public int Id { get; internal set; }
    public string? Name { get; internal set; }
    /// <summary>True for a buff, false for a debuff.</summary>
    public bool? IsBuff { get; internal set; }
    public int Stacks { get; internal set; }
    /// <summary>Seconds; null when the buff has no timer.</summary>
    public float? TimeLeft { get; internal set; }
    /// <summary>Seconds; null where the mode doesn't keep it (online).</summary>
    public float? Duration { get; internal set; }
}

/// <summary>A skill bar slot (1–5, 6 = evade) with its ability and cooldown. Unknown values are null.</summary>
internal sealed class Slot
{
    public int Number { get; internal set; }
    public string? AbilityId { get; internal set; }
    public string? Name { get; internal set; }
    public bool Channelled { get; internal set; }
    /// <summary>The game's ability, for the dispatcher; valid this frame only.</summary>
    public Ability? Ability { get; internal set; }
    /// <summary>Not on cooldown, as the game decides when the key is pressed.</summary>
    public bool? Ready { get; internal set; }
    public bool? HasCooldown { get; internal set; }
    public float? Charges { get; internal set; }
    public int? MaxCharges { get; internal set; }
    /// <summary>Seconds until the next charge; 0 when full.</summary>
    public float? NextChargeIn { get; internal set; }
    /// <summary>Seconds one charge takes.</summary>
    public float? RechargeTime { get; internal set; }
}

/// <summary>The player's form: a transform ability's id (null in human form) and whether the Reaper form is on.</summary>
internal sealed class Form
{
    public int? TransformId { get; internal set; }
    public bool? Reaper { get; internal set; }
}

/// <summary>Potion charges; null when no source can be read.</summary>
internal sealed class Potions
{
    public int? Charges { get; internal set; }
    public int? MaxCharges { get; internal set; }
}
