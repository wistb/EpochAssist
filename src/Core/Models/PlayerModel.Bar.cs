using Il2Cpp;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
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
            SeeSkill(info.Id, info.Name);
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
        // Online the rate reads 0 for a moment after a cast, until the server's value arrives: the skill's last known
        // charge time stands in.
        float? one = rate > 0f ? 1f / rate : info.Id != null && s_rechargeTimes.TryGetValue(info.Id, out float known) ? known : null;
        if (rate > 0f && info.Id != null) s_rechargeTimes[info.Id] = 1f / rate;
        if (current >= maxCharges) slot.NextChargeIn = 0f;
        else if (one is float time) slot.NextChargeIn = (MathF.Floor(current) + 1f - current) * time;
        slot.RechargeTime = one;
    }

    // One charge's time per skill id, as last read with a rate (dropped with the player: stats change it).
    private static readonly Dictionary<string, float> s_rechargeTimes = new();

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
}
