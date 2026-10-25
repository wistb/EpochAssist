namespace EpochAssist.Dev;

// Reply size for the inspection commands: a long reply is cut, and the whole text goes to <command>.txt in the dev
// folder, as probe.txt does.
internal static class DevOutput
{
    public const int MaxReplyLines = 40;
    public const int MaxMembers = 400;
    public const int MaxItems = 20;

    /// <summary>The first line and up to 40 more; anything longer is written whole to <command>.txt.</summary>
    public static string Reply(string command, IReadOnlyList<string> lines)
    {
        if (lines.Count <= MaxReplyLines + 1) return string.Join("\n", lines);
        string file = command + ".txt";
        Directory.CreateDirectory(DevBridge.Dir);
        File.WriteAllLines(Path.Combine(DevBridge.Dir, file), lines);
        return string.Join("\n", lines.Take(MaxReplyLines + 1)) + $"\n… full text in {file} ({lines.Count} lines)";
    }
}
