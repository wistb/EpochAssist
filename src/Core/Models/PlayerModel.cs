using System.Diagnostics;
using Il2Cpp;
using Il2CppEHG.Multiplayer;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>
/// The local player's state as one snapshot per frame, the same shape online and offline. Each part is filled on its
/// first read in a frame, so every reader that frame sees the same values and unread parts cost nothing. A part whose
/// group is missing, or whose read fails this frame, reads as null. Objects handed out are valid for this frame only.
/// </summary>
internal static partial class PlayerModel
{
    public const string VitalsGroup = "player: vitals";
    public const string WardOffline = "player: ward (offline)";
    public const string WardOnline = "player: ward (online)";
    public const string ModeFlag = "player: mode flag";
    public const string BuffsOffline = "player: buffs (offline)";
    public const string BuffsOnline = "player: buffs (online)";
    public const string SkillBar = "player: skill bar";
    public const string CooldownReady = "player: cooldown ready";
    public const string ChargesOffline = "player: cooldown charges (offline)";
    public const string ChargesOnline = "player: cooldown charges (online)";
    public const string FormGroup = "player: form";
    public const string ReaperForm = "player: reaper form";
    public const string PotionsOffline = "player: potions (offline)";
    public const string PotionsOnline = "player: potions (online)";

    public const int SlotCount = 6;
    private const float UiLookupSeconds = 1f;

    // One part: its values, the frame they are from, and whether this frame's read failed.
    private abstract class Part
    {
        public readonly string Name;
        public int Frame = -1;
        public bool Failed;

        protected Part(string name) => Name = name;

        public abstract void Fill();
        public abstract void Clear();
    }

    private sealed class VitalsPart : Part
    {
        public readonly Vitals Value = new();
        public VitalsPart() : base("vitals") { }
        public override void Fill() => FillVitals(Value);
        public override void Clear() { }
    }

    private sealed class BuffsPart : Part
    {
        public readonly List<Buff> Value = new();
        public readonly List<Buff> Pool = new();
        public BuffsPart() : base("buffs") { }
        public override void Fill() => FillBuffs(this);
        public override void Clear()
        {
            Pool.AddRange(Value);
            Value.Clear();
        }
    }

    private sealed class BarPart : Part
    {
        public readonly Slot[] Slots = new Slot[SlotCount];
        public readonly Slot?[] Value = new Slot?[SlotCount];
        public bool? Channelling;
        public BarPart() : base("skill bar")
        {
            for (int i = 0; i < SlotCount; i++) Slots[i] = new Slot { Number = i + 1 };
        }
        public override void Fill() => FillBar(this);
        public override void Clear()
        {
            Channelling = null;
            for (int i = 0; i < SlotCount; i++)
            {
                Value[i] = null;
                Slots[i].Ability = null;
            }
        }
    }

    private sealed class FormPart : Part
    {
        public readonly Form Value = new();
        public FormPart() : base("form") { }
        public override void Fill() => FillForm(Value);
        public override void Clear() { }
    }

    private sealed class PotionsPart : Part
    {
        public readonly Potions Value = new();
        public PotionsPart() : base("potions") { }
        public override void Fill() => FillPotions(Value);
        public override void Clear() { }
    }

    private static readonly VitalsPart s_vitals = new();
    private static readonly BuffsPart s_buffs = new();
    private static readonly BarPart s_bar = new();
    private static readonly FormPart s_form = new();
    private static readonly PotionsPart s_potions = new();
    private static readonly Part[] s_parts = { s_vitals, s_buffs, s_bar, s_form, s_potions };

    private static Feature? s_feature;
    // Each part's declared groups, from the feature's requirements at start.
    private static readonly Dictionary<string, List<string>> s_partGroups = new();

    // Components of the player object, kept until the next reset and looked up again when gone.
    private static GameObject? s_object;
    private static PlayerMana? s_mana;
    private static ClientWardHolder? s_ward;
    private static UsingAbilityPlayer? s_using;
    private static ClientUsingAbilityState? s_clientUsing;
    private static ClientAbilityList? s_clientAbilities;
    private static PlayerChargeManager? s_charges;
    private static OfflinePlayerChargeManager? s_offlineCharges;
    private static ClientChargeManager? s_clientCharges;
    private static HealthPotionCharges? s_potion;
    private static BuffsUIManager? s_buffsUi;
    private static Cooldown s_uiLookup;
    private static int s_modeFrame = -1;
    private static bool? s_online;

    /// <summary>Fill time per frame over the last second.</summary>
    public static CostWindow Cost { get; } = new();

    /// <summary>A local player is in game and the model runs.</summary>
    public static bool Exists => Running && Player.Exists;

    /// <summary>The mode: the game's own flag where it reads, else the player object's name; null without a player.</summary>
    public static bool? Online
    {
        get
        {
            if (!Exists) return null;
            int frame = Time.frameCount;
            if (frame == s_modeFrame) return s_online;
            s_modeFrame = frame;
            s_online = Player.Online;
            if (HasGroup(ModeFlag))
            {
                try
                {
                    s_online = GameplayEnvironment.IsOnlinePlay;
                }
                catch (Exception e)
                {
                    Log.ErrorThrottled("player:mode", $"Player model mode read failed: {e.Message}", 30);
                }
            }
            return s_online;
        }
    }

    public static Vitals? Vitals => Read(s_vitals, "vitals") ? s_vitals.Value : null;

    /// <summary>Buffs and debuffs; an empty list means none.</summary>
    public static IReadOnlyList<Buff>? Buffs => Read(s_buffs, "buffs") ? s_buffs.Value : null;

    /// <summary>The six bar slots in bar order; a slot that couldn't be read this frame is null.</summary>
    public static IReadOnlyList<Slot?>? Slots => Read(s_bar, "skill bar") ? s_bar.Value : null;

    /// <summary>Slot 1–5, or 6 for evade.</summary>
    public static Slot? Slot(int number) => number is >= 1 and <= SlotCount && Read(s_bar, "skill bar") ? s_bar.Value[number - 1] : null;

    /// <summary>The player is channelling a skill now.</summary>
    public static bool? Channelling => Read(s_bar, "skill bar") ? s_bar.Channelling : null;

    public static Form? Form => Read(s_form, "form") ? s_form.Value : null;

    public static Potions? Potions => Read(s_potions, "potions") ? s_potions.Value : null;

    /// <summary>True when the part's group for the current mode is present (the part may still fail a read).</summary>
    public static bool Has(string part) => Running && MissingGroup(part) == null;

    /// <summary>The missing group that turns the part off in the current mode, or null.</summary>
    public static string? MissingGroup(string part)
    {
        var feature = s_feature;
        if (feature == null) return $"player: {part}";
        if (!s_partGroups.TryGetValue(part, out var groups)) s_partGroups[part] = groups = Compat.PartGroups(feature, part);
        if (groups.Count == 0) return $"player: {part}";
        return Compat.MissingPartGroup(feature, groups, Online == true);
    }

    /// <summary>True when the part's last read this frame threw.</summary>
    public static bool Failed(string part)
    {
        foreach (var p in s_parts)
        {
            if (p.Name == part) return p.Frame == Time.frameCount && p.Failed;
        }
        return false;
    }

    /// <summary>Drops every value and component; the next read finds the player again.</summary>
    public static void Reset()
    {
        foreach (var part in s_parts)
        {
            part.Frame = -1;
            part.Failed = false;
            part.Clear();
        }
        s_object = null;
        s_mana = null;
        s_ward = null;
        s_using = null;
        s_clientUsing = null;
        s_tree = null;
        s_clientAbilities = null;
        s_retry.Clear();
        s_charges = null;
        s_offlineCharges = null;
        s_clientCharges = null;
        s_potion = null;
        s_buffsUi = null;
        s_abilityInfo.Clear();
        s_rechargeTimes.Clear();
        s_reaper = null;
        s_reaperLookup.Clear();
        s_uiLookup.Clear();
        s_modeFrame = -1;
        s_online = null;
        ResetCasts();
        ResetMovement();
    }

    internal static void Start(Feature feature)
    {
        s_feature = feature;
        s_partGroups.Clear();
        Cost.Reset();
        Reset();
    }

    internal static void Stop()
    {
        Reset();
        s_feature = null;
    }

    private static bool Running => s_feature?.State == FeatureState.Running;

    private static bool HasGroup(string group) => s_feature != null && s_feature.Has(group);

    // Fills the part once per frame; false when it isn't available or its read failed this frame.
    private static bool Read(Part part, string name)
    {
        if (!Exists || !Has(name)) return false;
        int frame = Time.frameCount;
        if (part.Frame != frame)
        {
            part.Frame = frame;
            part.Failed = false;
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (!Game.Alive(s_object) || s_object != Player.Object) DropComponents();
                part.Fill();
            }
            catch (Exception e)
            {
                part.Failed = true;
                part.Clear();
                Log.ErrorThrottled($"player:{part.Name}", $"Player model {part.Name} read failed: {e.Message}", 30);
            }
            Cost.Add(Stopwatch.GetTimestamp() - start);
        }
        return !part.Failed;
    }

    // A new player object (respawn, mode change) needs its own components.
    private static void DropComponents()
    {
        s_object = Player.Object;
        s_reaper = null;
        s_mana = null;
        s_ward = null;
        s_using = null;
        s_clientUsing = null;
        s_tree = null;
        s_clientAbilities = null;
        s_retry.Clear();
        s_charges = null;
        s_offlineCharges = null;
        s_clientCharges = null;
        s_potion = null;
    }

    // Online and offline players carry different components, so a lookup that finds nothing waits a second before it
    // is tried again rather than searching every frame.
    private static readonly Dictionary<Type, Cooldown> s_retry = new();
    private const float MissingRetrySeconds = 1f;

    private static T? Component<T>(ref T? cached) where T : Component
    {
        if (Game.Alive(cached)) return cached;
        cached = null;
        if (s_object == null || (s_retry.TryGetValue(typeof(T), out var retry) && !retry.Ready)) return null;
        cached = s_object.GetComponent<T>();
        if (Game.Alive(cached)) return cached;
        cached = null;
        retry.Start(MissingRetrySeconds);
        s_retry[typeof(T)] = retry;
        return null;
    }
}
