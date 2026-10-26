using Il2Cpp;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    // Buff ids seen on the player since the game started (zones and model restarts keep them), for the buff picker.
    private static readonly HashSet<int> s_seenBuffs = new();

    /// <summary>Every buff (not debuff) id seen on the player this session.</summary>
    public static IReadOnlyCollection<int> SeenBuffs => s_seenBuffs;

    private static void FillBuffs(BuffsPart part)
    {
        part.Clear();
        if (Online == true) FillBuffsOnline(part);
        else FillBuffsOffline(part);
        foreach (var buff in part.Value)
        {
            if (buff.IsBuff != false) s_seenBuffs.Add(buff.Id);
        }
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
}
