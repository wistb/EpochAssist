using System.Globalization;
using UnityEngine;

namespace EpochAssist.Core;

// The release commands that need only the core: help, actions, run, set, compat, log, profiles and mods (owner host).
internal static class CoreCommands
{
    private const int SettleFrames = 2;
    private const int DefaultLogCount = 50, MaxLogCount = 500;

    /// <summary>Set by the dev bridge: a feature that requires what the game lacks, for compat's self-test line.</summary>
    public static Func<Feature>? CompatSelfTest { get; set; }

    public static void Register()
    {
        Add("help", "help [command]", "Lists the commands, or shows one command's usage.", Help);
        Add("actions", "actions", "Lists every action with its kind and state.", _ => CommandResult.Ok("actions: " + string.Join(", ", Actions.All.Select(a =>
            $"{a.Name} ({(a.IsToggle ? "toggle" : "run")}{(Actions.StateOf(a) is bool on ? on ? ", on" : ", off" : string.Empty)})"))));
        // Action names keep their case (hud.togglePanel), so the argument isn't lowered.
        Add("run", "run <action>", "Runs an action by name.", call =>
            new CommandResult(Actions.Run(call.Args.Length > 0 ? call.Args[0] : string.Empty, call.Trigger)) { Settle = SettleFrames });
        Add("set", "set <setting id> <value>", "Changes a setting; the id may start with its group id and a dot.", Set);
        Add("compat", "compat", "Lists the game members each feature misses.", Compat);
        Add("log", "log [error|warning|info|debug] [count]", "Shows the newest log lines at a level or more severe.", LogLines);
        Add("profiles", "profiles [use|new|copy|delete <name>] [rename <name> to <new name>] [export [segment ...]] [import <file> [new]]",
            "Lists the profiles, or picks, makes, copies, renames, deletes, exports or imports one.", ProfilesCommand);
        Commands.Register(new Command("mods", "mods", "Lists the loaded mods, other mods' patches on Epoch Assist's targets, and known overlaps.", CommandMark.Release, "host", Mods)
        {
            DevVerbs = new[] { "simulate" },
        });
    }

    private static void Add(string name, string usage, string help, Func<CommandCall, CommandResult> run) =>
        Commands.Register(new Command(name, usage, help, CommandMark.Release, "host", run));

    private static CommandResult Help(CommandCall call)
    {
        if (call.Args.Length == 0)
        {
            return CommandResult.Ok("commands: " + string.Join("; ", Commands.Visible(Runtime.Hosted)
                .Select(c => $"{c.Name} - {c.Help}{(c.Mark == CommandMark.Dev ? " (dev)" : string.Empty)}")));
        }
        var command = Commands.Visible(Runtime.Hosted).FirstOrDefault(c => c.Name == call.Lower(1));
        return command == null ? CommandResult.Error($"help: no command {call.Args[0]}") : CommandResult.Ok($"{command.Usage}: {command.Help}");
    }

    // set <id> <value>, where id is a setting's Id, optionally prefixed by its group id and a dot.
    private static CommandResult Set(CommandCall call)
    {
        string id = call.Lower(1), value = call.Rest(2);
        var setting = SettingsRegistry.Groups.SelectMany(g => g.All)
            .FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                || $"{s.Section.Group.Id}.{s.Id}".Equals(id, StringComparison.OrdinalIgnoreCase));
        if (setting == null) return CommandResult.Error($"set {id}: no such setting") with { Settle = SettleFrames };
        bool set = setting switch
        {
            Toggle t when bool.TryParse(value, out bool b) => Apply(() => t.Value = b),
            Number n when float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && f >= n.Min && f <= n.Max => Apply(() => n.Value = n.Snap(f)),
            Choice<int> c when int.TryParse(value, out int i) && c.Options.Any(o => o.Value == i) => Apply(() => c.Value = i),
            Key k when Enum.TryParse(value, true, out KeyCode key) => Apply(() => k.Value = key),
            Hotkey h when Chord.TryParse(value, h.Kind, out var chord) && (h.Clearable || !chord.IsEmpty) => Apply(() => h.Value = chord.Text),
            Choice<KeyCode> c when Enum.TryParse(value, true, out KeyCode key) && c.Options.Any(o => o.Value == key) => Apply(() => c.Value = key),
            Choice<string> c when c.Options.FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) is { Value: { } match } => Apply(() => c.Value = match),
            Pick pick when value.Length > 0 => Apply(() => pick.Value = value),
            ColorSetting color when value.Length == 0 || ColorSetting.TryParse(value.ToUpperInvariant(), out _) => Apply(() => color.Value = value.ToUpperInvariant()),
            TargetSetting targets => targets.SetText(value, out _),
            // Comma-separated ids; empty (or "") is the natural order.
            OrderSetting order => Apply(() => order.Value = value.Trim('"').Replace(" ", string.Empty)),
            _ => false,
        };
        // Outside Default the value went into the profile in use.
        var result = set ? CommandResult.Ok(call.Line + (Profiles.InProfile ? $" (profile '{Profiles.Active.Name}')" : string.Empty))
            : CommandResult.Error($"set {setting.Id}: allowed {Allowed(setting)}");
        return result with { Settle = SettleFrames };
    }

    private static bool Apply(Action set)
    {
        set();
        return true;
    }

    private static string Allowed(Setting setting) => setting switch
    {
        Toggle => "true or false",
        Number n => $"{n.Min.ToString(CultureInfo.InvariantCulture)}–{n.Max.ToString(CultureInfo.InvariantCulture)}",
        Choice<int> c => string.Join(", ", c.Options.Select(o => $"{o.Value} ({o.Label})")),
        Choice<KeyCode> c => string.Join(", ", c.Options.Select(o => o.Value.ToString())),
        Choice<string> c => string.Join(", ", c.Options.Select(o => $"{o.Value} ({o.Label})")),
        Key => "a key name, such as F2",
        Hotkey h => (h.Clearable ? "nothing, or " : string.Empty) + "key names joined by +, such as LeftControl+F2",
        Pick pick => pick.Options?.Invoke() is { Count: > 0 } names ? string.Join(", ", names) : "a name",
        ColorSetting => "#RRGGBB, or nothing",
        TargetSetting => "a slot target document, such as {\"v\":1}",
        OrderSetting => "ids joined by commas, such as macros,interact; nothing for the natural order",
        _ => "nothing",
    };

    private static CommandResult Compat(CommandCall call)
    {
        var report = FeatureHost.All.Select(f => (f.Id, Missing: Core.Compat.Missing(f))).ToList();
        if (CompatSelfTest != null) report.Add(("selftest", Core.Compat.Missing(CompatSelfTest())));
        return CommandResult.Ok("compat: " + string.Join("; ", report.Select(r =>
            $"{r.Id}: {(r.Missing.Count == 0 ? "ok" : string.Join(", ", r.Missing.Select(m => m.Group == null ? m.Text : $"{m.Text} [optional {m.Group}]")))}")));
    }

    // profiles: list | use <name> | new <name> | copy <name> (the profile in use) | rename <name> to <new> | delete <name>.
    private static CommandResult ProfilesCommand(CommandCall call)
    {
        string verb = call.Lower(1), rest = call.Rest(2);
        if (verb.Length == 0)
        {
            return CommandResult.Ok("profiles: " + string.Join(", ", Profiles.All.Select(p =>
                $"{p.Name}{(p.Id == Profiles.Active.Id ? " (in use)" : string.Empty)} [{Profiles.Users(p.Id)} characters]")));
        }
        string reason;
        switch (verb)
        {
            case "use" when Profiles.Find(rest) is { } profile:
                return Profiles.Use(profile.Id, out reason) ? CommandResult.Ok($"profiles use {profile.Name}") with { Settle = SettleFrames } : CommandResult.Error($"profiles use: {reason}");
            case "new" or "copy":
                return Profiles.Create(rest, verb == "copy", out reason) is { } made ? CommandResult.Ok($"profiles {verb} {made.Name}") : CommandResult.Error($"profiles {verb}: {reason}");
            case "rename":
                int to = rest.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
                if (to < 0 || Profiles.Find(rest.Substring(0, to)) is not { } old) return CommandResult.Error("profiles rename: usage profiles rename <name> to <new name>");
                return Profiles.Rename(old.Id, rest.Substring(to + 4), out reason) ? CommandResult.Ok($"profiles rename {old.Name} to {rest.Substring(to + 4).Trim()}") : CommandResult.Error($"profiles rename: {reason}");
            case "delete" when Profiles.Find(rest) is { } profile:
                return Profiles.Delete(profile.Id, out reason) ? CommandResult.Ok($"profiles delete {profile.Name}") : CommandResult.Error($"profiles delete: {reason}");
            case "use" or "delete":
                return CommandResult.Error($"profiles {verb}: no profile {rest}");
            case "segments":
                return CommandResult.Ok("profiles segments: " + string.Join(", ", ProfileTransfer.Segments.Select(x => x.Id)));
            case "export":
                // Segment ids (group/section) to export, or every one.
                var segments = call.Args.Length > 1 ? call.Args.Skip(1).ToList() : ProfileTransfer.Segments.Select(x => x.Id).ToList();
                string? file = ProfileTransfer.WriteFile(ProfileTransfer.ExportText(ProfileTransfer.KeysOf(segments)), out reason);
                return file != null ? CommandResult.Ok($"profiles export: {segments.Count} segments to {file}") : CommandResult.Error($"profiles export: {reason}");
            case "import":
                bool asNew = call.Lower(call.Args.Length) == "new";
                string name = asNew ? string.Join(' ', call.Args.Skip(1).SkipLast(1)) : rest;
                if (ProfileTransfer.ReadFile(name, out reason) is not { } export) return CommandResult.Error($"profiles import: {reason}");
                if (asNew)
                {
                    if (Profiles.Create(ProfilesFeature.NextName(export.Name), copy: false, out reason) is not { } imported) return CommandResult.Error($"profiles import: {reason}");
                    Profiles.Use(imported.Id, out _);
                }
                var (set, skipped) = ProfileTransfer.Apply(export, ProfileTransfer.KeysOf(export.Segments));
                string migrated = (export.Migrated.Count > 0 ? $"; migrated {string.Join(", ", export.MigratedNames)}" : string.Empty)
                    + (export.Unreadable.Count > 0 ? $"; kept conditions this version can't read: {string.Join(", ", export.Unreadable)}" : string.Empty);
                return CommandResult.Ok($"profiles import: {set} set, {skipped} skipped, into {Profiles.Active.Name}{migrated}") with { Settle = SettleFrames };
            default:
                return CommandResult.Error("profiles: usage profiles [use|new|copy|delete <name>] [rename <name> to <new name>]");
        }
    }

    // The Diagnostics snapshot's three sections, "none" when empty.
    private static CommandResult Mods(CommandCall call)
    {
        var report = ModOverlap.Check();
        var text = new System.Text.StringBuilder($"ok mods: {report.Mods.Count} loaded, {report.Overlaps.Count} overlaps");
        foreach (var (title, lines) in new[] { ("mods", report.ModLines.ToList()), ("overlaps", report.OverlapLines.ToList()), ("known overlaps", report.KnownLines.ToList()) })
        {
            text.Append('\n').Append(title).Append(lines.Count == 0 ? ": none" : ":");
            foreach (string line in lines) text.Append("\n  ").Append(line);
        }
        return new CommandResult(Diagnostics.Scrub(text.ToString()));
    }

    // log [level] [count], in either order; the level defaults to the setting's.
    private static CommandResult LogLines(CommandCall call)
    {
        int level = Log.Level, count = DefaultLogCount;
        for (int i = 1; i <= call.Args.Length; i++)
        {
            string arg = call.Lower(i);
            int named = Array.IndexOf(new[] { "error", "warning", "info", "debug" }, arg);
            if (named >= 0) level = named;
            else if (int.TryParse(arg, out int n) && n > 0) count = Math.Min(n, MaxLogCount);
            else return CommandResult.Error("log: usage log [error|warning|info|debug] [count]");
        }
        var lines = Log.Recent(level, count);
        var text = new System.Text.StringBuilder($"ok log: {lines.Count} lines");
        foreach (var line in lines)
            text.Append('\n').Append($"{line.At:HH:mm:ss} {Log.LevelName(line.Level)} ").Append(line.Text.Replace("\r", string.Empty).Replace("\n", "\n    "));
        return new CommandResult(text.ToString());
    }
}
