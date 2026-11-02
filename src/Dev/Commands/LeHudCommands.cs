using System.Reflection;
using EpochAssist.Core;
using Il2Cpp;
using EpochAssist.Features;

namespace EpochAssist.Dev;

// lehud: test items for the stash search checks, made by the LEHud mod's force drop (its own legality checks, the
// game's own affix, seal and corruption code) and dropped at the player. Dev builds only, the saved copy only, and
// only while LEHud is loaded; nothing of LEHud is referenced at build time.
internal static class LeHudCommands
{
    public const string Usage = "lehud drop <type> <sub> <rarity> [unique <id>] [affix <id>:<tier>:<roll>[:regular|primordial]]… "
        + "[corruption <id>:<tier>:<roll>] [corrupted] [fp <n>] [lp <n>] [implicits <r,r,r>] [rolls <r,…>] [illegal] | lehud bases <name part> | lehud affix <name part> "
        + "| lehud planner list|drop [from <n>] [max <n>] [build <name part>]";

    private const string Ns = "LastEpoch_Hud.Scripts.";

    public static CommandResult Run(CommandCall call) => call.Lower(1) switch
    {
        "drop" => Drop(call),
        "bases" => Bases(call.Rest(2).ToLowerInvariant()),
        "affix" => AffixFind(call.Rest(2).ToLowerInvariant()),
        "planner" => PlannerItems(call),
        "shape" => Shape(),
        "types" => Types(),
        _ => CommandResult.Error($"lehud: usage {Usage}"),
    };

    private static Assembly? LeHud() =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "LastEpoch_Hud");

    // Rarity is the game's byte: below 7 the affix count decides it (LEHud's rule); 7 unique, 8 set.
    private static CommandResult Drop(CommandCall call)
    {
        if (GameState.Online != false) return CommandResult.Error("lehud drop: saved copy only");
        if (LeHud() is not { } asm) return CommandResult.Error("lehud drop: LEHud isn't loaded");
        if (!int.TryParse(call.Lower(2), out int type) || !int.TryParse(call.Lower(3), out int sub) || !int.TryParse(call.Lower(4), out int rarity))
            return CommandResult.Error($"lehud: usage {Usage}");
        var affixType = asm.GetType(Ns + "Core.ForceDrop.ResolvedForceDropAffix");
        var dropType = asm.GetType(Ns + "Core.ForceDrop.ResolvedForceDrop");
        var sealType = asm.GetType(Ns + "Core.ForceDrop.ForceDropSeal");
        var modeType = asm.GetType(Ns + "Core.ForceDrop.ForceDropMode");
        var creator = asm.GetType(Ns + "ModUI.ForceDrop.ForceDropItemCreator");
        if (affixType == null || dropType == null || sealType == null || modeType == null || creator == null)
            return CommandResult.Error("lehud drop: this LEHud has no force drop types (v5.1.0 expected)");

        int unique = 0, fp = 0, lp = 0;
        bool corrupted = false, illegal = false;
        var affixes = new List<object>();
        object? corruption = null;
        var implicits = new List<int> { 128, 128, 128 };
        var rolls = Enumerable.Repeat(128, 8).ToList();
        object NewAffix(string spec, string seal)
        {
            var p = spec.Split(':');
            if (p.Length < 3) throw new FormatException($"affix {spec}: id:tier:roll");
            if (p.Length > 3) seal = p[3];
            object sealValue = Enum.Parse(sealType, seal, true);
            return Activator.CreateInstance(affixType, int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), sealValue)!;
        }
        try
        {
            for (int i = 5; i <= call.Args.Length; i++)
            {
                string a = call.Lower(i);
                string Next() => i < call.Args.Length ? call.Lower(++i) : throw new FormatException($"{a} needs a value");
                switch (a)
                {
                    case "unique": unique = int.Parse(Next()); break;
                    case "affix": affixes.Add(NewAffix(Next(), "None")); break;
                    case "corruption": corruption = NewAffix(Next(), "None"); break;
                    case "corrupted": corrupted = true; break;
                    case "illegal": illegal = true; break;
                    case "fp": fp = int.Parse(Next()); break;
                    case "lp": lp = int.Parse(Next()); break;
                    case "implicits": implicits = Next().Split(',').Select(int.Parse).ToList(); break;
                    case "rolls": rolls = Next().Split(',').Select(int.Parse).ToList(); break;
                    default: return CommandResult.Error($"lehud drop: unknown part {a}");
                }
            }
            var typedAffixes = Array.CreateInstance(affixType, affixes.Count);
            for (int i = 0; i < affixes.Count; i++) typedAffixes.SetValue(affixes[i], i);
            object request = Activator.CreateInstance(dropType, type, sub, unique, rarity, fp, lp, 0, corrupted,
                implicits, rolls, typedAffixes, new List<int>(), corruption, Enum.ToObject(modeType, illegal ? 1 : 0))!;
            creator.GetMethod("Drop", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new[] { request });
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            return CommandResult.Error($"lehud drop: {e.InnerException.Message}");
        }
        catch (Exception e)
        {
            return CommandResult.Error($"lehud drop: {e.Message}");
        }
        return CommandResult.Ok($"lehud drop: type {type} sub {sub} rarity {rarity}, {affixes.Count} affixes{(corruption != null ? ", a corrupted affix" : string.Empty)}{(corrupted ? ", corrupted" : string.Empty)} dropped at the player");
    }

    // Base types: id, name, subtype count.
    private static CommandResult Types()
    {
        var list = ItemList.get();
        if (list == null) return CommandResult.Error("lehud types: no item list");
        var lines = new List<string> { "ok lehud types:" };
        foreach (var b in list.EquippableItems)
            if (b != null) lines.Add($"  {b.baseTypeID} {b.BaseTypeName} ({b.subItems?.Count ?? 0})");
        return CommandResult.Ok(string.Join("\n", lines));
    }

    // The force drop request's shape (constructor parameters), for matching this LEHud's version.
    private static CommandResult Shape()
    {
        if (LeHud() is not { } asm) return CommandResult.Error("lehud shape: LEHud isn't loaded");
        var lines = new List<string> { "ok lehud shape:" };
        foreach (string name in new[] { "Core.ForceDrop.ResolvedForceDrop", "Core.ForceDrop.ForceDropMode", "Core.ForceDrop.ForceDropSeal" })
        {
            var t = asm.GetType(Ns + name);
            if (t == null) continue;
            if (t.IsEnum) lines.Add($"  {t.Name}: {string.Join(", ", Enum.GetNames(t))}");
            foreach (var c in t.GetConstructors()) lines.Add($"  {t.Name}({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
        }
        foreach (var t in asm.GetTypes().Where(t => t.IsEnum && (t.Name.Contains("Route") || t.Name.Contains("LegendaryType"))))
            lines.Add($"  {t.FullName}: {string.Join(", ", Enum.GetNames(t))}");
        return CommandResult.Ok(string.Join("\n", lines));
    }

    // lehud planner list|drop: every complete planned item of every imported build and stage (gear, idols, weaver items;
    // blessings aren't stash items), each distinct item once, made in LEHud's legal mode only: a piece the game or LEHud
    // wouldn't allow is skipped and named, never forced. Drops in batches (from, max) so the ground stays readable.
    private static CommandResult PlannerItems(CommandCall call)
    {
        bool drop = call.Lower(2) == "drop";
        int from = 0, max = int.MaxValue;
        string? buildPart = null;
        for (int i = 3; i <= call.Args.Length; i++)
        {
            string a = call.Lower(i);
            if (a == "from" && int.TryParse(call.Lower(i + 1), out int f)) { from = f; i++; }
            else if (a == "max" && int.TryParse(call.Lower(i + 1), out int m)) { max = m; i++; }
            else if (a == "build") { buildPart = call.Rest(i + 1).Trim().ToLowerInvariant(); break; }
        }
        var items = new List<(string Where, PlannedItem Item)>();
        var seen = new HashSet<string>();
        int incomplete = 0;
        foreach (var build in Features.Planner.Library.Builds)
        {
            if (buildPart != null && !build.Name.ToLowerInvariant().Contains(buildPart)) continue;
            for (int stage = 0; stage < build.Stages.Count; stage++)
            {
                var gear = Features.Planner.GearOf(build, stage);
                var pieces = gear.Slots.Values.Concat(gear.Idols).Concat(gear.WeaverItems.Values);
                foreach (var item in pieces)
                {
                    Features.Planner.Check(item);
                    if (item.Unmakeable || item.Unknown.Count > 0) { incomplete++; continue; }
                    string key = $"{item.ItemType}/{item.SubType}/{item.Unique}/{item.Rarity}/{item.Corrupted}/"
                        + string.Join(",", item.Affixes.Select(x => $"{x.Id}:{x.Tier}:{x.Roll}:{x.Sealed}")) + "/" + string.Join(",", item.Implicits) + "/" + string.Join(",", item.UniqueRolls);
                    if (seen.Add(key)) items.Add(($"{build.Name} · {build.Stages[stage].Name}", item));
                }
            }
        }
        var lines = new List<string> { $"ok lehud planner: {items.Count} distinct complete items ({incomplete} incomplete pieces left out)" };
        int dropped = 0, refused = 0;
        foreach (var (where, item) in items.Skip(from).Take(max))
        {
            string what = $"{where}: {(item.Name.Length > 0 ? item.Name : $"type {item.ItemType} sub {item.SubType}")} ({item.Affixes.Count} affixes{(item.Unique != 0 ? $", unique {item.Unique}" : string.Empty)})";
            if (!drop) { lines.Add("  " + what); continue; }
            string? error = DropPlanned(item, out string route);
            if (error == null) { dropped++; if (route.Length > 0) lines.Add($"  {what}{route}"); }
            else { refused++; lines.Add($"  skipped {what}: {error}"); }
        }
        if (drop) lines[0] += $"; dropped {dropped}, refused {refused} (items {from}–{from + dropped + refused - 1})";
        return new CommandResult(DevOutput.Reply("lehud-planner", lines.Take(200).ToList()));
    }

    // One planned item through LEHud's force drop, legal mode: every affix with its tier, roll and seal, the corrupted
    // affix, implicits and unique rolls as planned. Null when dropped, else why not.
    // A legendary's added affixes are legal by one route only, and the planner doesn't say which: refused for "another
    // route", it is tried as a Weaver's Will item, then with Legendary Potential (still legal mode).
    private static string? DropPlanned(PlannedItem item, out string route)
    {
        route = string.Empty;
        string? error = DropPlanned(item, 0, 0);
        if (error == null || !error.Contains("another route")) return error;
        if (DropPlanned(item, 0, 20) == null) { route = " (as Weaver's Will 20)"; return null; }
        if (DropPlanned(item, 4, 0) == null) { route = " (with Legendary Potential 4)"; return null; }
        return error;
    }

    private static string? DropPlanned(PlannedItem item, int lp, int ww)
    {
        if (GameState.Online != false) return "saved copy only";
        if (LeHud() is not { } asm) return "LEHud isn't loaded";
        var affixType = asm.GetType(Ns + "Core.ForceDrop.ResolvedForceDropAffix");
        var dropType = asm.GetType(Ns + "Core.ForceDrop.ResolvedForceDrop");
        var sealType = asm.GetType(Ns + "Core.ForceDrop.ForceDropSeal");
        var modeType = asm.GetType(Ns + "Core.ForceDrop.ForceDropMode");
        var creator = asm.GetType(Ns + "ModUI.ForceDrop.ForceDropItemCreator");
        if (affixType == null || dropType == null || sealType == null || modeType == null || creator == null) return "this LEHud has no force drop types";
        try
        {
            object Affix(PlannedAffix a, string seal) => Activator.CreateInstance(affixType, (int)a.Id, (int)a.Tier, (int)a.Roll, Enum.Parse(sealType, seal, true))!;
            var regular = new List<object>();
            object? corruption = null;
            foreach (var a in item.Affixes)
            {
                switch (a.Sealed)
                {
                    case SealedAffixType.FromCorruption: corruption = Affix(a, "Corruption"); break;
                    case SealedAffixType.Regular: regular.Add(Affix(a, "Regular")); break;
                    case SealedAffixType.Primordial: regular.Add(Affix(a, "Primordial")); break;
                    default: regular.Add(Affix(a, "None")); break;
                }
            }
            var typed = Array.CreateInstance(affixType, regular.Count);
            for (int i = 0; i < regular.Count; i++) typed.SetValue(regular[i], i);
            // LEHud keeps three implicit rolls whatever the base has: the planned ones first, the rest at their best.
            var implicits = item.Implicits.Select(b => (int)b).Take(3).ToList();
            while (implicits.Count < 3) implicits.Add(255);
            var rolls = item.UniqueRolls.Select(b => (int)b).ToList();
            while (rolls.Count < 8) rolls.Add(255);
            object request = Activator.CreateInstance(dropType, (int)item.ItemType, (int)item.SubType, (int)item.Unique, (int)item.Rarity, 0, lp, ww,
                item.Corrupted || corruption != null, implicits, rolls, typed, new List<int>(), corruption, Enum.ToObject(modeType, 0))!;
            object? result = creator.GetMethod("Drop", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new[] { request });
            return result is bool ok && !ok ? "LEHud refused it" : result is string why && why.Length > 0 ? why : null;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            return e.InnerException.Message;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    // Base types and subtypes by name, with their grid size.
    private static CommandResult Bases(string want)
    {
        var list = ItemList.get();
        if (list == null) return CommandResult.Error("lehud bases: no item list");
        var lines = new List<string>();
        foreach (var b in list.EquippableItems)
        {
            if (b?.subItems == null) continue;
            foreach (var s in b.subItems)
            {
                string text = $"{b.BaseTypeName} / {s.displayName} ({s.name})";
                if (want.Length == 0 || text.ToLowerInvariant().Contains(want))
                    lines.Add($"  type {b.baseTypeID} sub {s.subTypeID} {b.gridSize.x}x{b.gridSize.y}: {text}");
            }
        }
        lines.Insert(0, $"ok lehud bases: {lines.Count}");
        return new CommandResult(DevOutput.Reply("lehud-bases", lines.Take(60).ToList()));
    }

    private static CommandResult AffixFind(string want)
    {
        var lines = new List<string>();
        foreach (var a in AffixList.get().AllAffixes)
        {
            string name = a?.getAffixDisplayName() ?? string.Empty;
            if (a == null || !name.ToLowerInvariant().Contains(want)) continue;
            lines.Add($"  {a.affixId} \"{name}\" {a.type} {a.specialAffixType} rollsOn {a.rollsOn} tiers {a.tiers?.Count} on [{(a.canRollOn == null ? string.Empty : string.Join(",", a.canRollOn.ToArray()))}] class {a.classSpecificity}");
        }
        lines.Insert(0, $"ok lehud affix: {lines.Count}");
        return new CommandResult(DevOutput.Reply("lehud-affix", lines.Take(60).ToList()));
    }
}
