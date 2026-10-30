using EpochAssist.Core;
using EpochAssist.Ui;

namespace EpochAssist.Dev;

// Dev tools for module loading, shared by the modules command and the dev window's Modules section: the choices, the
// next start's kind (fresh install, upgrade), modules made unavailable, the first-start toast, and a reload to apply.
// The next start's kind and the unavailable list are process variables: they survive a dev reload, never the game.
internal static class ModuleCommands
{
    public const string Usage = "modules [status] | set <id|all> on|off | reload | fresh | upgrade | unavailable <id>|off | toast | tools on|off";

    /// <summary>The window section stays up without the Modules page open.</summary>
    public static bool Pinned;

    public static CommandResult Run(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "" or "status":
                return new CommandResult(DevOutput.Reply("modules", StatusLines().ToList()));
            case "set" when call.Lower(3) is "on" or "off":
                return Set(call.Lower(2), call.Lower(3) == "on") is { } error ? CommandResult.Error("modules set: " + error) : CommandResult.Ok($"modules set {call.Lower(2)} {call.Lower(3)}; reload to apply");
            case "reload":
                return Reload(call, null);
            case "fresh":
                return Reload(call, "fresh");
            case "upgrade":
                return Reload(call, "upgrade");
            case "unavailable" when call.Lower(2) == "off":
                ClearUnavailable();
                return CommandResult.Ok("modules unavailable: none at the next reload");
            case "unavailable" when Modules.Find(call.Lower(2)) != null:
                ToggleUnavailable(call.Lower(2));
                return CommandResult.Ok($"modules unavailable: {UnavailableText()} at the next reload");
            case "toast":
                ModulesPage.ShowToast();
                return CommandResult.Ok("modules toast");
            case "tools" when call.Lower(2) is "on" or "off":
                Pinned = call.Lower(2) == "on";
                return CommandResult.Ok($"modules tools {call.Lower(2)}");
            default:
                return CommandResult.Error("modules: usage " + Usage);
        }
    }

    public static IEnumerable<string> StatusLines()
    {
        yield return $"ok modules: start {ModuleChoices.Decision}, prompt {(Modules.Pending ? "open" : "answered")}, files {(ModuleChoices.FilesFresh() ? "fresh" : "not fresh")}, unavailable at reload {UnavailableText()}";
        foreach (var module in Modules.All)
        {
            string state = FeatureHost.Find(module.Id) is { } feature ? feature.State.ToString().ToLowerInvariant() : "not loaded";
            yield return $"  {module.Id}: choice {(ModuleChoices.Choice(module.Id) ? "on" : "off")}, {state}{(Modules.RestartNeeded(module.Id) && !Modules.Pending ? ", restart to apply" : string.Empty)}";
        }
    }

    /// <summary>Sets a module's choice (or every one's); null when done, else why not.</summary>
    public static string? Set(string id, bool on)
    {
        if (Modules.Pending) return "the first-start choice is open; answer it on the Modules page";
        var ids = id == "all" ? Modules.All.Select(m => m.Id).ToList() : new List<string> { id };
        foreach (string each in ids)
        {
            if (ModuleChoices.For(each) is not { } toggle) return $"no module {each}";
            toggle.Value = on;
        }
        return null;
    }

    /// <summary>Starts again as a dev reload does; kind "fresh" or "upgrade" makes that start one.</summary>
    public static CommandResult Reload(CommandCall? call, string? kind)
    {
        Environment.SetEnvironmentVariable(ModuleChoices.DevStartVariable, kind);
        Config.Save();
        if (call?.Source == "bridge") DevBridge.ReloadAfterBatch = true;
        else Runtime.ReloadRequested = true;
        return CommandResult.Ok($"modules: reload{(kind != null ? " as " + kind : string.Empty)} requested");
    }

    public static bool IsUnavailable(string id) => Compat.Unavailable().Contains(id);

    public static void ToggleUnavailable(string id)
    {
        var set = Compat.Unavailable();
        if (!set.Remove(id)) set.Add(id);
        Environment.SetEnvironmentVariable(Compat.UnavailableVariable, set.Count == 0 ? null : string.Join(",", set));
    }

    public static void ClearUnavailable() => Environment.SetEnvironmentVariable(Compat.UnavailableVariable, null);

    private static string UnavailableText()
    {
        var set = Compat.Unavailable();
        return set.Count == 0 ? "none" : string.Join(", ", set);
    }
}
