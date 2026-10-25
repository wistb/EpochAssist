using System.Diagnostics;
using Il2Cpp;
using Il2CppEHG.Multiplayer;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

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

/// <summary>Model fill time per frame over the last second, counting only frames with work.</summary>
internal sealed class CostWindow
{
    private const int Capacity = 2048;
    private readonly long[] _ticks = new long[Capacity];
    private readonly float[] _at = new float[Capacity];
    private int _next;
    private int _frame = -1;
    private long _current;

    /// <summary>Adds time spent in this frame.</summary>
    public void Add(long ticks)
    {
        int frame = Time.frameCount;
        if (frame != _frame) Close(frame);
        _current += ticks;
    }

    public void Reset()
    {
        Array.Clear(_at);
        Array.Clear(_ticks);
        _next = 0;
        _frame = -1;
        _current = 0;
    }

    public double AverageMs => Window(out _);

    public double WorstMs
    {
        get
        {
            Window(out double worst);
            return worst;
        }
    }

    private void Close(int frame)
    {
        if (_frame >= 0)
        {
            _ticks[_next] = _current;
            _at[_next] = Time.unscaledTime;
            _next = (_next + 1) % Capacity;
        }
        _frame = frame;
        _current = 0;
    }

    // The frames closed within the last second, plus this frame's work so far.
    private double Window(out double worstMs)
    {
        float since = Time.unscaledTime - 1f;
        long sum = 0, worst = 0;
        int frames = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (_at[i] <= since || _at[i] == 0f) continue;
            sum += _ticks[i];
            worst = Math.Max(worst, _ticks[i]);
            frames++;
        }
        if (_current > 0)
        {
            sum += _current;
            worst = Math.Max(worst, _current);
            frames++;
        }
        worstMs = FrameTimes.Ms(worst);
        return frames > 0 ? FrameTimes.Ms(sum) / frames : 0d;
    }
}

/// <summary>
/// The local player's state as one snapshot per frame, the same shape online and offline. Each part is filled on its
/// first read in a frame, so every reader that frame sees the same values and unread parts cost nothing. A part whose
/// group is missing, or whose read fails this frame, reads as null. Objects handed out are valid for this frame only.
/// </summary>
internal static class PlayerModel
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
        s_clientAbilities = null;
        s_retry.Clear();
        s_charges = null;
        s_offlineCharges = null;
        s_clientCharges = null;
        s_potion = null;
        s_buffsUi = null;
        s_abilityInfo.Clear();
        s_reaper = null;
        s_reaperLookup.Clear();
        s_uiLookup.Clear();
        s_modeFrame = -1;
        s_online = null;
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

    private static void FillVitals(Vitals vitals)
    {
        PlayerHealth health = Player.Health ?? throw new InvalidOperationException("no PlayerHealth");
        PlayerMana mana = Component(ref s_mana) ?? throw new InvalidOperationException("no PlayerMana");
        vitals.Health = health.currentHealth;
        vitals.MaxHealth = health.maxHealth;
        vitals.HealthFraction = health.getHealthPercent();
        vitals.Mana = mana.currentMana;
        vitals.MaxMana = mana.maxMana;
        vitals.Ward = ReadWard();
    }

    // Offline the actor's protection; online the player object's ward holder.
    private static float? ReadWard()
    {
        if (!Has("ward")) return null;
        try
        {
            if (Online != true)
            {
                ProtectionClass? protection = Player.Actor?.protection;
                return Game.Alive(protection) ? protection!.currentWard : null;
            }
            return Component(ref s_ward) is { } ward ? ward.currentWard : null;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("player:ward", $"Player model ward read failed: {e.Message}", 30);
            return null;
        }
    }

    private static void FillBuffs(BuffsPart part)
    {
        part.Clear();
        if (Online == true) FillBuffsOnline(part);
        else FillBuffsOffline(part);
    }

    private static Buff NextBuff(BuffsPart part)
    {
        Buff buff;
        if (part.Pool.Count > 0)
        {
            buff = part.Pool[^1];
            part.Pool.RemoveAt(part.Pool.Count - 1);
        }
        else
        {
            buff = new Buff();
        }
        part.Value.Add(buff);
        return buff;
    }

    // Ailments grouped by id, keeping the ones the buff bar shows. Stacks are the receiver's count where it keeps one
    // (one instance can carry several), else the instances; the time is the longest instance's.
    private static void FillBuffsOffline(BuffsPart part)
    {
        AilmentReceiver receiver = Player.Actor?.ailmentReceiver ?? throw new InvalidOperationException("no ailment receiver");
        var ailments = receiver.ailments;
        var stacks = receiver.ailmentStacks;
        if (ailments == null) return;
        for (int i = 0; i < ailments.Count; i++)
        {
            var instance = ailments[i];
            Ailment? ailment = instance?.ailment;
            if (instance == null || !Game.Alive(ailment) || !ailment!.showsInBuffUI) continue;
            int id = (int)ailment.id;
            Buff? buff = null;
            foreach (var existing in part.Value)
            {
                if (existing.Id == id) buff = existing;
            }
            float remaining = instance.remainingDuration;
            if (buff == null)
            {
                buff = NextBuff(part);
                buff.Id = id;
                buff.Name = GameData.Buff(id).Name;
                buff.IsBuff = ailment.positive;
                buff.Stacks = 0;
                buff.TimeLeft = null;
                buff.Duration = null;
            }
            buff.Stacks++;
            if (remaining >= 0f && (buff.TimeLeft == null || remaining > buff.TimeLeft))
            {
                buff.TimeLeft = remaining;
                buff.Duration = instance.totalDuration > 0f ? instance.totalDuration : null;
            }
        }
        if (stacks == null) return;
        foreach (var buff in part.Value)
        {
            var key = (AilmentID)buff.Id;
            if (stacks.ContainsKey(key) && stacks[key] > 0) buff.Stacks = stacks[key];
        }
    }

    // The buff bar's own entries; it keeps buffs and debuffs apart but not the full duration.
    private static void FillBuffsOnline(BuffsPart part)
    {
        BuffsUIManager ui = BuffsUi() ?? throw new InvalidOperationException("no buff bar");
        AddEntries(part, ui.buffs, true);
        AddEntries(part, ui.debuffs, false);
    }

    private static void AddEntries(BuffsPart part, Il2CppSystem.Collections.Generic.List<BuffUIEntry>? entries, bool isBuff)
    {
        if (entries == null) return;
        for (int i = 0; i < entries.Count; i++)
        {
            BuffUIEntry? entry = entries[i];
            if (!Game.Alive(entry)) continue;
            var buff = NextBuff(part);
            buff.Id = entry!.id;
            buff.Name = GameData.Buff(entry.id).Name;
            buff.IsBuff = isBuff;
            buff.Stacks = Math.Max(1, entry.stacks);
            float left = entry.maxRemainingDuration;
            buff.TimeLeft = left >= 0f ? left : null;
            buff.Duration = null;
        }
    }

    // A UI object, not a singleton: looked up at most once a second while missing.
    private static BuffsUIManager? BuffsUi()
    {
        if (Game.Alive(s_buffsUi)) return s_buffsUi;
        s_buffsUi = null;
        if (!s_uiLookup.Ready) return null;
        s_uiLookup.Start(UiLookupSeconds);
        s_buffsUi = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.Of<BuffsUIManager>())?.TryCast<BuffsUIManager>();
        return Game.Alive(s_buffsUi) ? s_buffsUi : null;
    }

    // Per fill: the ability each bar number shows, and where each slot's charges are in the manager's lists.
    private static readonly Ability?[] s_barAbilities = new Ability?[SlotCount];
    private static readonly int[] s_chargeIndex = new int[SlotCount];
    // An ability's id, name and channelling don't change, so they are read once per ability object.
    private static readonly Dictionary<IntPtr, (string? Id, string? Name, bool Channelled)> s_abilityInfo = new();

    private static (string? Id, string? Name, bool Channelled) Info(Ability ability)
    {
        if (!s_abilityInfo.TryGetValue(ability.Pointer, out var info))
        {
            info = (ability.playerAbilityID, ability.abilityName, ability.channelled);
            s_abilityInfo[ability.Pointer] = info;
        }
        return info;
    }

    private static void FillBar(BarPart part)
    {
        part.Clear();
        Array.Clear(s_barAbilities);
        // The bar icon knows exactly which ability its slot shows, online and offline.
        var icons = Game.BarIcons;
        for (int i = 0; icons != null && i < icons.Count; i++)
        {
            AbilityBarIcon? icon = icons[i];
            int number = icon != null ? icon.abilityNumber : 0;
            if (number is >= 1 and <= SlotCount && s_barAbilities[number - 1] == null) s_barAbilities[number - 1] = icon!.ability;
        }

        bool online = Online == true;
        bool ready = Has("cooldown ready");
        var charges = Has("cooldown charges") ? ChargeLists(online) : default;
        PlayerAbilityList? abilityList = null;
        for (int i = 0; i < SlotCount; i++)
        {
            var slot = part.Slots[i];
            try
            {
                Ability? ability = s_barAbilities[i];
                if (!Game.Alive(ability))
                {
                    // The fallback AutoCast used: the ability list's slot.
                    abilityList ??= Game.LocalTree?.playerAbilityList;
                    ability = abilityList?.getAbility(i);
                }
                FillSlot(slot, Game.Alive(ability) ? ability : null, ready, online, charges, i);
                part.Value[i] = slot;
            }
            catch (Exception)
            {
                // The bar is rebuilding (form change, respec, loading): unknown this frame.
                slot.Ability = null;
                part.Value[i] = null;
            }
        }

        // Online the client's own ability state; it isn't a UsingAbilityPlayer.
        if (online) part.Channelling = Component(ref s_clientUsing) is { } client ? client.channelling : null;
        else part.Channelling = Component(ref s_using) is { } usingAbility ? usingAbility.channelling : null;
    }

    private static void FillSlot(Slot slot, Ability? ability, bool readReady, bool online,
        (Il2CppSystem.Collections.Generic.List<float>? Charges, Il2CppSystem.Collections.Generic.List<float>? Max, Il2CppSystem.Collections.Generic.List<float>? Regen) lists, int i)
    {
        slot.Ability = ability;
        slot.Ready = null;
        slot.HasCooldown = null;
        slot.Charges = null;
        slot.MaxCharges = null;
        slot.NextChargeIn = null;
        slot.RechargeTime = null;
        if (ability == null)
        {
            slot.AbilityId = slot.Name = null;
            slot.Channelled = false;
            return;
        }
        var info = Info(ability);
        slot.AbilityId = info.Id;
        slot.Name = info.Name;
        slot.Channelled = info.Channelled;

        if (readReady && !online && Component(ref s_charges) is { } manager) slot.Ready = !manager.onCoooldown(ability);
        if (lists.Charges == null) return;
        int index = s_chargeIndex[i];
        if (index < 0 || index >= lists.Charges.Count || index >= lists.Max!.Count || index >= lists.Regen!.Count) return;

        float current = lists.Charges[index], rate = lists.Regen[index];
        int maxCharges = (int)Math.Round(lists.Max[index]);
        slot.Charges = current;
        slot.MaxCharges = maxCharges;
        slot.HasCooldown = maxCharges > 0;
        // Online the client's charge manager isn't a PlayerChargeManager, so ready is what onCoooldown means offline:
        // no cooldown, or a charge to spend.
        if (readReady && online) slot.Ready = maxCharges <= 0 || current >= 1f;
        if (maxCharges <= 0) return;
        if (current >= maxCharges) slot.NextChargeIn = 0f;
        else if (rate > 0f) slot.NextChargeIn = (MathF.Floor(current) + 1f - current) / rate;
        if (rate > 0f) slot.RechargeTime = 1f / rate;
    }

    // The charge lists and each slot's index in them. Online they follow the manager's bar icons; offline the
    // manager's own ability list, in its own order.
    private static (Il2CppSystem.Collections.Generic.List<float>?, Il2CppSystem.Collections.Generic.List<float>?, Il2CppSystem.Collections.Generic.List<float>?) ChargeLists(bool online)
    {
        Array.Fill(s_chargeIndex, -1);
        if (online)
        {
            ClientChargeManager? manager = Component(ref s_clientCharges);
            if (manager == null) return default;
            var managerIcons = manager.icons;
            for (int k = 0; managerIcons != null && k < managerIcons.Count; k++)
            {
                AbilityBarIcon? icon = managerIcons[k];
                int number = icon != null ? icon.abilityNumber : 0;
                if (number is >= 1 and <= SlotCount) s_chargeIndex[number - 1] = k;
            }
            return (manager.charges, manager.maxCharges, manager.chargeRegen);
        }

        OfflinePlayerChargeManager? offline = Component(ref s_offlineCharges);
        if (offline == null) return default;
        var abilities = offline.abilities;
        for (int k = 0; abilities != null && k < abilities.Count; k++)
        {
            Ability? listed = abilities[k];
            if (listed == null) continue;
            for (int i = 0; i < SlotCount; i++)
            {
                Ability? shown = s_barAbilities[i];
                if (shown != null && (shown.Pointer == listed.Pointer || Info(shown).Id == Info(listed).Id)) s_chargeIndex[i] = k;
            }
        }
        return (offline.charges, offline.maxCharges, offline.chargeRegen);
    }

    private static void FillForm(Form form)
    {
        if (Online == true)
        {
            // Online there is no PlayerAbilityList; the client's list says whether the player is transformed. Which
            // transform it is wasn't seen online (no transform character), so a transformed player reads unknown.
            ClientAbilityList clientList = Component(ref s_clientAbilities) ?? throw new InvalidOperationException("no client ability list");
            if (clientList.transformed) throw new InvalidOperationException("transformed online: transform id not known");
            form.TransformId = null;
        }
        else
        {
            PlayerAbilityList list = Game.LocalTree?.playerAbilityList ?? throw new InvalidOperationException("no ability list");
            form.TransformId = TransformId(list);
        }
        form.Reaper = null;
        if (!Has("reaper form")) return;
        try
        {
            form.Reaper = ReaperMaterial() is { } material && material.materialToChangeTo == UIGlobeHealth.AlternateMaterial.ReaperForm;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("player:reaper", $"Player model Reaper form read failed: {e.Message}", 30);
        }
    }

    // The game hands an empty Nullable<int> back boxed as null, which the interop getter can't unbox: in human form the
    // getter throws a NullReferenceException (seen in 1.5.3). Throwing every frame is slow, so the backing field is read
    // in place: its hasValue flag and value at the offsets il2cpp reports. The getter is the fallback.
    private static int s_formHasValueOffset = -2, s_formValueOffset;

    private static int? TransformId(PlayerAbilityList list)
    {
        if (s_formHasValueOffset == -2) FindFormOffsets();
        if (s_formHasValueOffset >= 0)
        {
            IntPtr field = list.Pointer + s_formHasValueOffset;
            return System.Runtime.InteropServices.Marshal.ReadByte(field) != 0
                ? System.Runtime.InteropServices.Marshal.ReadInt32(list.Pointer + s_formValueOffset)
                : null;
        }
        try
        {
            var id = list.TransformAbilityId;
            return id.HasValue ? id.Value : null;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static void FindFormOffsets()
    {
        s_formHasValueOffset = -1;
        try
        {
            IntPtr owner = Il2CppInterop.Runtime.Il2CppClassPointerStore<PlayerAbilityList>.NativeClassPtr;
            IntPtr field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(owner, "<TransformAbilityId>k__BackingField");
            if (field == IntPtr.Zero) return;
            IntPtr nullable = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_from_type(Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_type(field));
            IntPtr hasValue = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(nullable, "hasValue");
            IntPtr value = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(nullable, "value");
            if (hasValue == IntPtr.Zero || value == IntPtr.Zero) return;
            // Field offsets in a value type count its object header, which an embedded struct doesn't have.
            int header = 2 * IntPtr.Size;
            int at = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
            s_formValueOffset = at + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(value) - header;
            s_formHasValueOffset = at + (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(hasValue) - header;
            Log.Debug($"Player model: transform id at +{s_formHasValueOffset} (has) / +{s_formValueOffset} (value)");
        }
        catch (Exception e)
        {
            s_formHasValueOffset = -1;
            Log.Warn($"Player model: transform id field not found, using the getter: {e.Message}");
        }
    }

    // A search of the player's children is slow, so while the component is absent it is searched for at most twice a
    // second; once found it is kept while it lives. Entering the Reaper form can show up to half a second late.
    private const float ReaperLookupSeconds = 0.5f;
    private static ChangeHealthMaterialDuringLifetime? s_reaper;
    private static Cooldown s_reaperLookup;

    private static ChangeHealthMaterialDuringLifetime? ReaperMaterial()
    {
        if (Game.Alive(s_reaper)) return s_reaper;
        s_reaper = null;
        if (s_object == null || !s_reaperLookup.Ready) return null;
        s_reaperLookup.Start(ReaperLookupSeconds);
        s_reaper = s_object.GetComponentInChildren<ChangeHealthMaterialDuringLifetime>();
        return Game.Alive(s_reaper) ? s_reaper : null;
    }

    // HealthPotion (offline) is a HealthPotionCharges, the component online; each mode reads its own.
    private static void FillPotions(Potions potions)
    {
        potions.Charges = null;
        potions.MaxCharges = null;
        if (!Game.Alive(s_potion)) s_potion = null;
        if (s_potion == null && s_object != null)
        {
            s_potion = Online == true ? s_object.GetComponent<HealthPotionCharges>() : s_object.GetComponent<HealthPotion>();
        }
        if (!Game.Alive(s_potion)) return;
        potions.Charges = s_potion!.currentCharges;
        potions.MaxCharges = s_potion.maxCharges;
    }
}

internal sealed class PlayerModelFeature : Feature
{
    public override string Id => "player";
    public override string Name => "Player model";
    public override string[] Needs => new[] { "game" };

    // One group per part, or one per mode where the source differs; a missing one turns off only that part.
    public override Requirement[] Requirements => new[]
    {
        Need<PlayerHealth>(nameof(PlayerHealth.currentHealth), PlayerModel.VitalsGroup),
        Need<PlayerHealth>(nameof(PlayerHealth.maxHealth), PlayerModel.VitalsGroup),
        Need<PlayerHealth>(nameof(PlayerHealth.getHealthPercent), PlayerModel.VitalsGroup),
        Need<PlayerMana>(nameof(PlayerMana.currentMana), PlayerModel.VitalsGroup),
        Need<PlayerMana>(nameof(PlayerMana.maxMana), PlayerModel.VitalsGroup),

        Need<Actor>(nameof(Actor.protection), PlayerModel.WardOffline),
        Need<ProtectionClass>(nameof(ProtectionClass.currentWard), PlayerModel.WardOffline),
        Need<ClientWardHolder>(nameof(ClientWardHolder.currentWard), PlayerModel.WardOnline),

        Need(typeof(GameplayEnvironment), nameof(GameplayEnvironment.IsOnlinePlay), PlayerModel.ModeFlag),

        Need<Actor>(nameof(Actor.ailmentReceiver), PlayerModel.BuffsOffline),
        Need<AilmentReceiver>(nameof(AilmentReceiver.ailments), PlayerModel.BuffsOffline),
        Need<AilmentReceiver>(nameof(AilmentReceiver.ailmentStacks), PlayerModel.BuffsOffline),
        Need<AilmentReceiver.ActiveAilment>(nameof(AilmentReceiver.ActiveAilment.ailment), PlayerModel.BuffsOffline),
        Need<AilmentReceiver.ActiveAilment>(nameof(AilmentReceiver.ActiveAilment.remainingDuration), PlayerModel.BuffsOffline),
        Need<AilmentReceiver.ActiveAilment>(nameof(AilmentReceiver.ActiveAilment.totalDuration), PlayerModel.BuffsOffline),
        Need<Ailment>(nameof(Ailment.id), PlayerModel.BuffsOffline),
        Need<Ailment>(nameof(Ailment.positive), PlayerModel.BuffsOffline),
        Need<Ailment>(nameof(Ailment.showsInBuffUI), PlayerModel.BuffsOffline),

        Need<BuffsUIManager>(nameof(BuffsUIManager.buffs), PlayerModel.BuffsOnline),
        Need<BuffsUIManager>(nameof(BuffsUIManager.debuffs), PlayerModel.BuffsOnline),
        Need<BuffUIEntry>(nameof(BuffUIEntry.id), PlayerModel.BuffsOnline),
        Need<BuffUIEntry>(nameof(BuffUIEntry.maxRemainingDuration), PlayerModel.BuffsOnline),
        Need<BuffUIEntry>(nameof(BuffUIEntry.stacks), PlayerModel.BuffsOnline),

        Need<AbilityBarIcon>(nameof(AbilityBarIcon.all), PlayerModel.SkillBar),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.abilityNumber), PlayerModel.SkillBar),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.ability), PlayerModel.SkillBar),
        Need<Ability>(nameof(Ability.playerAbilityID), PlayerModel.SkillBar),
        Need<Ability>(nameof(Ability.abilityName), PlayerModel.SkillBar),
        Need<Ability>(nameof(Ability.channelled), PlayerModel.SkillBar),
        Need<UsingAbilityPlayer>(nameof(UsingAbilityPlayer.channelling), PlayerModel.SkillBar),
        Need<ClientUsingAbilityState>(nameof(ClientUsingAbilityState.channelling), PlayerModel.SkillBar),
        Need<LocalTreeData>(nameof(LocalTreeData.playerAbilityList), PlayerModel.SkillBar),
        Need<PlayerAbilityList>(nameof(PlayerAbilityList.getAbility), PlayerModel.SkillBar),

        Need<PlayerChargeManager>(nameof(PlayerChargeManager.onCoooldown), PlayerModel.CooldownReady),

        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.abilities), PlayerModel.ChargesOffline),
        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.charges), PlayerModel.ChargesOffline),
        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.maxCharges), PlayerModel.ChargesOffline),
        Need<OfflinePlayerChargeManager>(nameof(OfflinePlayerChargeManager.chargeRegen), PlayerModel.ChargesOffline),

        Need<ClientChargeManager>(nameof(ClientChargeManager.icons), PlayerModel.ChargesOnline),
        Need<ClientChargeManager>(nameof(ClientChargeManager.charges), PlayerModel.ChargesOnline),
        Need<ClientChargeManager>(nameof(ClientChargeManager.maxCharges), PlayerModel.ChargesOnline),
        Need<ClientChargeManager>(nameof(ClientChargeManager.chargeRegen), PlayerModel.ChargesOnline),

        Need<LocalTreeData>(nameof(LocalTreeData.playerAbilityList), PlayerModel.FormGroup),
        Need<PlayerAbilityList>(nameof(PlayerAbilityList.TransformAbilityId), PlayerModel.FormGroup),
        Need<ClientAbilityList>(nameof(ClientAbilityList.transformed), PlayerModel.FormGroup),

        Need<ChangeHealthMaterialDuringLifetime>(nameof(ChangeHealthMaterialDuringLifetime.materialToChangeTo), PlayerModel.ReaperForm),
        Need(typeof(UIGlobeHealth.AlternateMaterial), nameof(UIGlobeHealth.AlternateMaterial.ReaperForm), PlayerModel.ReaperForm),

        Need<HealthPotion>(nameof(HealthPotion.currentCharges), PlayerModel.PotionsOffline),
        Need<HealthPotion>(nameof(HealthPotion.maxCharges), PlayerModel.PotionsOffline),
        Need<HealthPotionCharges>(nameof(HealthPotionCharges.currentCharges), PlayerModel.PotionsOnline),
        Need<HealthPotionCharges>(nameof(HealthPotionCharges.maxCharges), PlayerModel.PotionsOnline),
    };

    public override void Start() => PlayerModel.Start(this);
    public override void Shutdown() => PlayerModel.Stop();
}
