using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

internal static partial class GameEvents
{
    // ---- Hits ----

    private static float? s_lastHealth;
    private static ProtectionClass? s_protection;
    private static IntPtr s_protectionPtr;
    private static float s_healthBefore, s_wardBefore;
    private static readonly Dictionary<IntPtr, string?> s_attackerNames = new();

    private static void UpdateHits(Feature feature, bool? online)
    {
        bool active = Wanted<HitTaken>(feature, s_hits, online);
        // Detail offline only: online the game's hit event never fires for the local player, so online
        // hits are the health edge. The patch is on only while hits are wanted.
        string? detail = active && online == false ? HitDetailOffline : null;
        FeatureHost.SetPatchGroup(feature, HitDetailOffline, detail != null);
        if (active != s_hits.Active)
        {
            s_hits.Active = active;
            s_lastHealth = null;
        }
        if (!active) return;
        if (detail != null) CacheProtection();

        float? health = PlayerModel.Vitals?.Health;
        float? last = s_lastHealth;
        s_lastHealth = health;
        // With detail on and its object found, the game's own calls are the hits, so a drop isn't counted twice.
        if (health is not float now || last is not float before || now >= before) return;
        bool detailLive = detail != null && feature.DemandGroupsOn.Contains(detail) && s_protectionPtr != IntPtr.Zero;
        if (detailLive) return;
        Bus.Raise(new HitTaken(before - now, null, null, null, null, null, false));
    }

    private static void CacheProtection()
    {
        if (Game.Alive(s_protection)) return;
        var actor = Player.Actor;
        s_protection = actor != null ? actor.protection : null;
        s_protectionPtr = Game.Alive(s_protection) ? s_protection!.Pointer : IntPtr.Zero;
    }

    private static void OnApplyDamagePrefix(ProtectionClass protection)
    {
        if (s_protectionPtr == IntPtr.Zero || protection.Pointer != s_protectionPtr) return;
        s_healthBefore = Player.Health is { } health ? health.currentHealth : 0f;
        s_wardBefore = protection.currentWard;
    }

    // The amounts are what the call took from health and ward: its return value can't be read through Harmony.
    private static void OnApplyDamage(ProtectionClass protection, DamageStats? stats, DamageSource? source)
    {
        if (s_protectionPtr == IntPtr.Zero || protection.Pointer != s_protectionPtr) return;
        float health = Player.Health is { } h ? h.currentHealth : s_healthBefore;
        float ward = protection.currentWard;
        string? attacker = null, ability = null;
        if (source != null)
        {
            var info = source.GetDamageSourceInfo();
            var used = info.Item2 ?? info.Item3 ?? info.Item4;
            ability = Game.Alive(used) && !string.IsNullOrEmpty(used!.abilityName) ? used.abilityName : null;
            attacker = AttackerName(info.Item5);
        }
        Bus.Raise(new HitTaken(Math.Max(0f, s_healthBefore - health), Math.Max(0f, s_wardBefore - ward), attacker, ability, DamageTypes(stats), null, true));
    }

    // The nameplate's name, on the attacker's visuals; kept per attacker for the scene.
    private static string? AttackerName(Actor? attacker)
    {
        if (!Game.Alive(attacker)) return null;
        IntPtr key = attacker!.Pointer;
        if (s_attackerNames.TryGetValue(key, out string? name)) return name;
        var info = attacker.GetComponentInChildren<ActorDisplayInformation>(true);
        name = Game.Alive(info) && !string.IsNullOrEmpty(info!.displayName) ? info.displayName : null;
        s_attackerNames[key] = name;
        return name;
    }

    private static readonly string[] s_typeNames = { "physical", "fire", "cold", "lightning", "necrotic", "void", "poison" };

    private static string[]? DamageTypes(DamageStats? stats)
    {
        var damage = stats?.damage;
        if (damage == null) return null;
        List<string>? types = null;
        for (int i = 0; i < damage.Length && i < s_typeNames.Length; i++)
        {
            if (damage[i] > 0f) (types ??= new List<string>()).Add(s_typeNames[i]);
        }
        return types?.ToArray();
    }
}
