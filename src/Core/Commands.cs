namespace EpochAssist.Core;

// Runtime only, never stored. Dev commands are listed and run only under the dev loader.
internal enum CommandMark { Release, Dev }

/// <summary>
/// A typed command for the console and the dev bridge: its name (one lowercase word), a usage line, one sentence of
/// help, its mark, the owner that registered it, and what it does. DevVerbs are sub-verbs (the first argument) that
/// count as dev commands of their own; their handlers are registered from dev code with Commands.AddDevVerb.
/// </summary>
internal sealed record Command(string Name, string Usage, string Help, CommandMark Mark, string Owner, Func<CommandCall, CommandResult> Run)
{
    public string[] DevVerbs { get; init; } = Array.Empty<string>();
}

/// <summary>A line as typed, split into the command's name and its arguments; Source is "console" or "bridge".</summary>
internal sealed class CommandCall
{
    public CommandCall(string line, string source)
    {
        Line = line;
        Source = source;
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Name = parts[0].ToLowerInvariant();
        Args = parts.Skip(1).ToArray();
    }

    public string Line { get; }
    public string Name { get; }
    /// <summary>The arguments as typed.</summary>
    public string[] Args { get; }
    public string Source { get; }

    /// <summary>The trigger to log for what the command runs.</summary>
    public string Trigger => Source == "bridge" ? "dev bridge" : "console";

    /// <summary>Argument i (from 1, the word after the name) lowercased, or empty.</summary>
    public string Lower(int i) => i >= 1 && i <= Args.Length ? Args[i - 1].ToLowerInvariant() : string.Empty;

    /// <summary>The arguments from i on (from 1), joined by spaces, keeping their case.</summary>
    public string Rest(int i) => string.Join(' ', Args.Skip(Math.Max(0, i - 1)));
}

/// <summary>
/// A command's reply: one or more lines, each starting "ok" or "error". Settle is how many frames the bridge waits
/// before its next command; Pending, when set, is asked once a frame until it gives the reply.
/// </summary>
internal sealed record CommandResult(string Text)
{
    public int Settle { get; init; }
    public Func<string?>? Pending { get; init; }

    public static CommandResult Ok(string text) => new("ok " + text);
    public static CommandResult Error(string text) => new("error " + text);
}

/// <summary>
/// Every registered command. Core release commands are registered at load with owner "host"; dev commands by the dev
/// bridge; a feature's own are removed when it stops, faults or is unavailable. Names match whole and ignore case.
/// </summary>
internal static class Commands
{
    private static readonly Dictionary<string, Command> s_commands = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Command, string Verb), (string Owner, Func<CommandCall, CommandResult> Run)> s_devVerbs = new();

    public static void Register(Command command)
    {
        if (s_commands.ContainsKey(command.Name)) Log.Warn($"Command {command.Name} registered again; the newer one replaces it");
        s_commands[command.Name] = command;
    }

    /// <summary>Handles one of a command's DevVerbs; dev code only, so release builds have none.</summary>
    public static void AddDevVerb(string command, string verb, string owner, Func<CommandCall, CommandResult> run) =>
        s_devVerbs[(command.ToLowerInvariant(), verb.ToLowerInvariant())] = (owner, run);

    public static void RemoveOwner(string owner)
    {
        foreach (string name in s_commands.Values.Where(c => c.Owner == owner).Select(c => c.Name).ToList()) s_commands.Remove(name);
        foreach (var key in s_devVerbs.Where(v => v.Value.Owner == owner).Select(v => v.Key).ToList()) s_devVerbs.Remove(key);
    }

    public static void Clear()
    {
        s_commands.Clear();
        s_devVerbs.Clear();
    }

    public static Command? Get(string name) => s_commands.TryGetValue(name, out var command) ? command : null;

    /// <summary>The commands a front end may list and run, sorted by name; dev ones only when hosted.</summary>
    public static IEnumerable<Command> Visible(bool hosted) =>
        s_commands.Values.Where(c => hosted || c.Mark == CommandMark.Release).OrderBy(c => c.Name, StringComparer.Ordinal);

    /// <summary>Runs a line; null for an empty one. A dev command or dev verb outside the dev loader is unknown.</summary>
    public static CommandResult? Run(string line, string source, bool hosted)
    {
        line = line.Trim();
        if (line.Length == 0) return null;
        var call = new CommandCall(line, source);
        if (Get(call.Name) is not { } command || (!hosted && (command.Mark == CommandMark.Dev || command.DevVerbs.Contains(call.Lower(1)))))
            return CommandResult.Error($"unknown command: {line}");
        try
        {
            return hosted && s_devVerbs.TryGetValue((command.Name.ToLowerInvariant(), call.Lower(1)), out var devVerb) ? devVerb.Run(call) : command.Run(call);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled($"command:{command.Name}", $"Command '{line}' failed: {e}");
            return CommandResult.Error($"{line}: {e.Message}");
        }
    }

    /// <summary>Asks a pending reply once; an error in it becomes the reply.</summary>
    public static string? Poll(Func<string?> pending, string line)
    {
        try
        {
            return pending();
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("command:pending", $"Command '{line}' failed: {e}");
            return $"error {line}: {e.Message}";
        }
    }
}
