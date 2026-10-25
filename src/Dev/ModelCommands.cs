using System.Globalization;
using EpochAssist.Core;

namespace EpochAssist.Dev;

// model: prints the player and world models, looks up game data, and simulates a missing model part.
internal static class ModelCommands
{
    public const string Usage = "model player [part] | world [count] [kind] | data <kind> <key> | miss <player|world|data|events|visuals|gameui|dispatch> <group> | clear";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public static CommandResult Run(CommandCall call) => call.Lower(1) switch
    {
        "player" => PlayerCommand(call),
        "world" => WorldCommand(call),
        "data" => Data(call),
        "miss" => Miss(call),
        _ => CommandResult.Error($"model: usage {Usage}"),
    };

    // model player [part]: the snapshot, one line per part, and what filling it cost.
    private static CommandResult PlayerCommand(CommandCall call)
    {
        if (FeatureHost.Find("player") is not { State: FeatureState.Running } feature)
            return CommandResult.Error($"model player: player model not running ({FeatureHost.Find("player")?.LastError ?? "not registered"})");
        if (!PlayerModel.Exists) return CommandResult.Ok("model player: no player");

        string part = call.Lower(2);
        int slotNumber = 0;
        if (part == "slot" && (!int.TryParse(call.Lower(3), out slotNumber) || slotNumber is < 1 or > PlayerModel.SlotCount))
            return CommandResult.Error("model player: usage model player slot <1-6>");
        if (part.Length > 0 && part is not ("vitals" or "buffs" or "slot" or "form" or "potions" or "channelling"))
            return CommandResult.Error("model player: part is vitals, buffs, slot <n>, form, potions or channelling");

        var lines = new List<string>
        {
            $"ok model player: online={PlayerModel.Online?.ToString() ?? "?"} frame={UnityEngine.Time.frameCount} "
                + $"t={UnityEngine.Time.unscaledTime.ToString("F2", s_inv)} build={Runtime.BuildId}",
        };
        bool all = part.Length == 0;
        if (all || part == "vitals") lines.Add(VitalsLine());
        if (all || part == "buffs") lines.Add(BuffsLine());
        for (int n = 1; n <= PlayerModel.SlotCount; n++)
        {
            if (all || slotNumber == n) lines.Add(SlotLine(n));
        }
        if (all || part == "channelling") lines.Add(Pad("channelling") + (Unavailable("skill bar") ?? PlayerModel.Channelling?.ToString() ?? "?"));
        if (all || part == "form") lines.Add(FormLine());
        if (all || part == "potions") lines.Add(PotionsLine());
        lines.Add(Pad("cost") + $"avg {PlayerModel.Cost.AverageMs.ToString("F3", s_inv)}ms worst {PlayerModel.Cost.WorstMs.ToString("F3", s_inv)}ms last 1s");
        return new CommandResult(DevOutput.Reply("model", lines));
    }

    private static string Pad(string label) => label.PadRight(12);

    // Why a part reads null: its group is missing, or its read failed this frame; null when it is available.
    private static string? Unavailable(string part)
    {
        if (PlayerModel.MissingGroup(part) is { } group) return $"not available ({group} missing)";
        return PlayerModel.Failed(part) ? "not available (read failed)" : null;
    }

    private static string VitalsLine()
    {
        var v = PlayerModel.Vitals;
        if (v == null) return Pad("vitals") + (Unavailable("vitals") ?? "not available (read failed)");
        string ward = v.Ward is float w ? Num1(w) : PlayerModel.MissingGroup("ward") is { } g ? $"? ({g} missing)" : "?";
        return Pad("vitals") + $"health {Num1(v.Health)}/{Num1(v.MaxHealth)} ({v.HealthFraction.ToString("F3", s_inv)})  ward {ward}  mana {Num1(v.Mana)}/{Num1(v.MaxMana)}";
    }

    private static string BuffsLine()
    {
        var buffs = PlayerModel.Buffs;
        if (buffs == null) return Pad("buffs") + (Unavailable("buffs") ?? "not available (read failed)");
        if (buffs.Count == 0) return Pad("buffs") + "none";
        return Pad("buffs") + string.Join("  ", buffs.Select(b =>
            $"{(b.Name ?? "?").Replace(" ", string.Empty)} ({b.Id}) {(b.IsBuff switch { true => "buff", false => "debuff", _ => "?" })} x{b.Stacks} "
            + $"{(b.TimeLeft is float left ? left.ToString("F2", s_inv) + "s" : "–")}/{(b.Duration is float d ? Num(d) + "s" : "–")}"));
    }

    private static string SlotLine(int number)
    {
        string label = Pad($"slot {number}");
        var slot = PlayerModel.Slot(number);
        if (slot == null) return label + (Unavailable("skill bar") ?? "not available (read failed)");
        if (slot.Ability == null) return label + "empty";
        var text = new System.Text.StringBuilder($"{slot.AbilityId} \"{slot.Name}\"{(slot.Channelled ? " channelled" : string.Empty)}");
        text.Append(" ready=").Append(slot.Ready?.ToString() ?? "?");
        if (slot.HasCooldown == false) text.Append(" no cooldown");
        else if (slot.HasCooldown == true)
        {
            text.Append($" charges {Num(slot.Charges ?? 0f)}/{slot.MaxCharges}");
            if (slot.NextChargeIn is float next && next > 0f) text.Append($" next {next.ToString("F2", s_inv)}s");
            if (slot.RechargeTime is float full) text.Append($" full {full.ToString("F2", s_inv)}s");
        }
        else text.Append(" charges ?");
        return label + text;
    }

    private static string FormLine()
    {
        var form = PlayerModel.Form;
        if (form == null) return Pad("form") + (Unavailable("form") ?? "not available (read failed)");
        string reaper = form.Reaper?.ToString() ?? (PlayerModel.MissingGroup("reaper form") is { } g ? $"? ({g} missing)" : "?");
        return Pad("form") + $"{(form.TransformId is int id ? $"transform {id}" : "human")}  reaper {reaper}";
    }

    private static string PotionsLine()
    {
        var potions = PlayerModel.Potions;
        if (potions == null) return Pad("potions") + (Unavailable("potions") ?? "not available (read failed)");
        return Pad("potions") + (potions.Charges is int c ? $"{c}/{potions.MaxCharges}" : "unknown");
    }

    private static string Num1(float value) => value.ToString("0.#", s_inv);

    // model world [count] [kind]: the nearest entries with every field, and what keeping the index current costs.
    private static CommandResult WorldCommand(CommandCall call)
    {
        if (!World.Running)
            return CommandResult.Error($"model world: world model not running ({FeatureHost.Find("world")?.LastError ?? "not registered"})");
        int count = 10;
        WorldKind? kind = null;
        foreach (string arg in new[] { call.Lower(2), call.Lower(3) })
        {
            if (arg.Length == 0) continue;
            if (int.TryParse(arg, out int n)) count = Math.Clamp(n, 1, 500);
            else if (Enum.TryParse(arg, true, out WorldKind k)) kind = k;
            else return CommandResult.Error($"model world: usage model world [count] [enemy|player|shrine|chest]");
        }
        World.WakeFor(5f);

        var entries = World.Entries.Where(e => !e.Gone).ToList();
        var byKind = entries.GroupBy(e => e.Kind).OrderBy(g => g.Key).Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var lines = new List<string>
        {
            $"ok model world: scene {scene.name} handle {World.SceneHandle}, {entries.Count} known ({entries.Count(e => e.Active)} active; {string.Join(", ", byKind)}), "
                + $"built {(World.BuiltAt > 0f ? (UnityEngine.Time.unscaledTime - World.BuiltAt).ToString("F1", s_inv) + "s ago" : "never")}, t={UnityEngine.Time.unscaledTime.ToString("F2", s_inv)}",
        };
        var shown = entries.Where(e => kind == null || e.Kind == kind).OrderBy(e => e.Distance).Take(count).ToList();
        for (int i = 0; i < shown.Count; i++) lines.Add($"#{i,-3}{EntryLine(shown[i])}");
        var cost = World.Cost;
        lines.Add(Pad("cost") + $"avg {cost.AverageMs.ToString("F3", s_inv)}ms worst {cost.WorstMs.ToString("F3", s_inv)}ms last 1s, overruns {cost.Overruns}, "
            + $"hot {cost.Hot} cold {cost.Cold} stale(>1s) {cost.Stale}; discovery {cost.DiscoveryMs.ToString("F3", s_inv)}ms, "
            + $"slowest {cost.SlowestMs.ToString("F3", s_inv)}ms ({cost.SlowestCause})");
        return new CommandResult(DevOutput.Reply("model", lines));
    }

    // Other players print no name: the model has none for them.
    private static string EntryLine(WorldEntry e)
    {
        var text = new System.Text.StringBuilder($"{e.Kind,-7}{e.Distance.ToString("F1", s_inv)}m  {(e.Active ? "active" : "inactive")}");
        if (e.Kind == WorldKind.Enemy) text.Append($"  {e.Rarity?.ToString() ?? "?"}");
        if (e.Kind != WorldKind.Player) text.Append($"  \"{e.Name ?? "?"}\"");
        if (e.Kind is WorldKind.Enemy or WorldKind.Player)
        {
            text.Append($"  {(e.Health is float h ? Num1(h) : "?")}/{(e.MaxHealth is float m ? Num1(m) : "?")}  ward {(e.Ward is float w ? Num1(w) : "?")}");
            if (e.Dead) text.Append("  dead");
        }
        if (e.Kind == WorldKind.Enemy)
        {
            if (e.Mods.Count > 0) text.Append($"  mods [{string.Join(", ", e.Mods.Select(m => m.Name ?? "?"))}]");
            text.Append($"  objective {e.Objective?.ToString() ?? "?"}");
        }
        if (e.Kind is WorldKind.Shrine or WorldKind.Chest) text.Append($"  used={e.Used?.ToString() ?? "?"}");
        text.Append($"  age {Math.Max(0, UnityEngine.Time.frameCount - e.RefreshedAt)}");
        return text.ToString();
    }

    // model miss <model> <group> | clear: makes a model group look missing (dev only) and restarts what it affects.
    private static CommandResult Miss(CommandCall call)
    {
        if (call.Lower(2) == "clear")
        {
            var models = Compat.Simulated.Select(e => e[..e.IndexOf(':')]).Distinct().ToList();
            int cleared = Compat.Simulated.Count;
            Compat.Simulated.Clear();
            foreach (string id in models) RestartModel(FeatureHost.Find(id)!);
            return CommandResult.Ok($"model miss clear: {cleared} cleared{(models.Count > 0 ? $", {string.Join(", ", models)} restarted" : string.Empty)}");
        }
        string modelId = call.Lower(2);
        string group = call.Rest(3).Trim().Trim('"');
        if (modelId.Length == 0 || group.Length == 0) return CommandResult.Error("model miss: usage model miss <model> <group> | clear");
        if (FeatureHost.Find(modelId) is not { } model || modelId is not ("player" or "world" or "data" or "events" or "visuals" or "gameui" or "dispatch"))
            return CommandResult.Error($"model miss: unknown model '{modelId}'");
        if (!Compat.DeclaresGroup(model, group)) return CommandResult.Error($"model miss: {modelId} has no group '{group}'");
        Compat.Simulated.Add($"{modelId}:{group}");
        RestartModel(model);
        return CommandResult.Ok($"model miss {modelId} \"{group}\": simulated missing, {modelId} restarted");
    }

    // The host restarts the model's dependents that it stopped; one it found unavailable waits for a restart of its own.
    private static void RestartModel(Feature model)
    {
        FeatureHost.Restart(model);
        foreach (var feature in FeatureHost.All)
        {
            if (feature.State != FeatureState.Running && feature.Parts.Any(p => p.Model == model.Id)) FeatureHost.Restart(feature);
        }
    }

    // model data <kind> <key>: one lookup, as game data answers it.
    private static CommandResult Data(CommandCall call)
    {
        string kind = call.Lower(2), key = call.Args.Length >= 3 ? call.Args[2] : string.Empty;
        string head = $"model data {kind} {key}".TrimEnd();
        switch (kind)
        {
            case "buff" when int.TryParse(key, out int id):
                return Reply(head, GameData.Buff(id));
            case "ability" when key.Length > 0:
                return Reply(head, GameData.Ability(key));
            case "mod" when int.TryParse(key, out int modKey):
                return Reply(head, GameData.MonsterMod(modKey));
            case "affix" when int.TryParse(key, out int affixId):
                int? tier = int.TryParse(call.Lower(4), out int t) ? t : null;
                byte? roll = byte.TryParse(call.Lower(5), out byte r) ? r : null;
                float modifier = float.TryParse(call.Lower(6), System.Globalization.NumberStyles.Float, s_inv, out float m) ? m : 0f;
                head = $"model data affix {affixId}{(tier != null ? $" {tier}" : string.Empty)}{(roll != null ? $" {roll}" : string.Empty)}{(modifier != 0f ? $" {Num(modifier)}" : string.Empty)}";
                return Reply(head, GameData.Affix(affixId, tier, roll, modifier));
            case "item" when call.Lower(3) == "hovered":
                var item = Hovered.LastItem?.TryCast<Il2Cpp.ItemDataUnpacked>();
                if (item == null) return CommandResult.Error("model data item: no hovered item (run hovered over an open item tooltip first)");
                return CommandResult.Ok($"{head}: {Describe(GameData.Item(item))}" + ItemAffixes(item));
            case "buff" or "ability" or "mod" or "affix" or "item":
                return CommandResult.Error($"model data {kind}: usage model data affix <id> [tier] [roll] [modifier] | mod <key> | ability <id> | buff <id> | item hovered");
            default:
                return CommandResult.Error($"model data: unknown kind '{kind}'");
        }
    }

    // One line per affix on the item, looked up with the item's effect modifier, so each can be held against the tooltip.
    private static string ItemAffixes(Il2Cpp.ItemDataUnpacked item)
    {
        float modifier = GameData.AffixModifier(item) ?? 0f;
        var text = new System.Text.StringBuilder($"\nmodifier {Num(modifier)}");
        var affixes = item.affixes;
        for (int i = 0; affixes != null && i < affixes.Count; i++)
        {
            var affix = affixes[i];
            text.Append($"\naffix {i} id {affix.affixId} tier {affix.affixTier} roll {affix.affixRoll}: ")
                .Append(Describe(GameData.Affix(affix.affixId, affix.affixTier, affix.affixRoll, modifier)));
        }
        return text.ToString();
    }

    private static CommandResult Reply(string head, DataEntry entry) => CommandResult.Ok($"{head}: {Describe(entry)}");

    /// <summary>A data entry as the model command prints it: the name in quotes and what else the kind carries.</summary>
    public static string Describe(DataEntry entry)
    {
        if (!entry.Known) return "unknown";
        var text = new System.Text.StringBuilder($"\"{entry.Name}\"");
        switch (entry.Kind)
        {
            case DataKind.MonsterMod:
                text.Append(" internal ").Append(entry.InternalName != null ? $"\"{entry.InternalName}\"" : "–");
                break;
            case DataKind.Affix:
                if (entry.Title != null) text.Append($" ({entry.Title})");
                if (entry.ShownTier is int shown) text.Append($" tier {shown}");
                if (entry.RangeMin is float min && entry.RangeMax is float max) text.Append($" range {Num(min)}–{Num(max)}");
                else if (entry.ShownTier != null) text.Append(" range unknown");
                if (entry.Value is float value) text.Append($" value {Num(value)}");
                break;
            case DataKind.Item:
                text.Append(" base ").Append(entry.BaseName != null ? $"\"{entry.BaseName}\"" : "–");
                text.Append(" unique ").Append(entry.UniqueName != null ? $"\"{entry.UniqueName}\"" : "–");
                break;
        }
        return text.ToString();
    }

    public static string Num(float value) => value.ToString("0.####", s_inv);
}
