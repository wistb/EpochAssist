using MelonLoader;

namespace EpochAssist.Core;

/// <summary>A line the log kept: when, its level (0 error … 3 debug) and its scrubbed text.</summary>
internal readonly record struct LogLine(DateTime At, int Level, string Text);

// The mod's one path to the log. Lines above the level setting are dropped; kept ones are scrubbed of personal data
// and also held in a ring of recent lines for the log command.
internal static class Log
{
    public const int ErrorLevel = 0, WarningLevel = 1, InfoLevel = 2, DebugLevel = 3;
    private const int RecentCount = 500;

    private static readonly MelonLogger.Instance s_logger = new(BuildInfo.Name);
    private static readonly Dictionary<string, DateTime> s_nextAllowed = new();
    private static readonly LogLine[] s_recent = new LogLine[RecentCount];
    private static int s_recentNext, s_recentCount;

    /// <summary>Lines above this level are dropped; follows the Log level setting.</summary>
    public static int Level = InfoLevel;

    public static void Error(string message) => Write(ErrorLevel, message);
    public static void Warn(string message) => Write(WarningLevel, message);
    public static void Info(string message) => Write(InfoLevel, message);
    public static void Debug(string message) => Write(DebugLevel, message);

    public static void ErrorThrottled(string key, string message, double seconds = 10) => Throttled(ErrorLevel, key, message, seconds);
    public static void WarnThrottled(string key, string message, double seconds = 10) => Throttled(WarningLevel, key, message, seconds);
    public static void InfoThrottled(string key, string message, double seconds = 10) => Throttled(InfoLevel, key, message, seconds);
    public static void DebugThrottled(string key, string message, double seconds = 10) => Throttled(DebugLevel, key, message, seconds);

    /// <summary>The newest kept lines at this level or more severe, oldest first.</summary>
    public static List<LogLine> Recent(int level, int count)
    {
        var lines = new List<LogLine>();
        lock (s_recent)
        {
            for (int i = 0; i < s_recentCount && lines.Count < count; i++)
            {
                var line = s_recent[(s_recentNext - 1 - i + RecentCount) % RecentCount];
                if (line.Level <= level) lines.Add(line);
            }
        }
        lines.Reverse();
        return lines;
    }

    public static string LevelName(int level) => level switch
    {
        ErrorLevel => "ERROR",
        WarningLevel => "WARNING",
        InfoLevel => "INFO",
        _ => "DEBUG",
    };

    private static void Throttled(int level, string key, string message, double seconds)
    {
        if (level <= Level && Allow(key, seconds)) Write(level, message);
    }

    private static void Write(int level, string message)
    {
        if (level > Level) return;
        string text = Diagnostics.Scrub(message);
        lock (s_recent)
        {
            s_recent[s_recentNext] = new LogLine(DateTime.Now, level, text);
            s_recentNext = (s_recentNext + 1) % RecentCount;
            s_recentCount = Math.Min(s_recentCount + 1, RecentCount);
        }
        switch (level)
        {
            case ErrorLevel: s_logger.Error(text); break;
            case WarningLevel: s_logger.Warning(text); break;
            default: s_logger.Msg(level == DebugLevel ? "[debug] " + text : text); break;
        }
    }

    private static bool Allow(string key, double seconds)
    {
        var now = DateTime.UtcNow;
        if (s_nextAllowed.TryGetValue(key, out var next) && now < next) return false;
        s_nextAllowed[key] = now.AddSeconds(seconds);
        return true;
    }
}
