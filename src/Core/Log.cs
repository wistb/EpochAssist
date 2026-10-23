using MelonLoader;

namespace EpochAssist.Core;

internal static class Log
{
    private static readonly MelonLogger.Instance s_logger = new(BuildInfo.Name);
    private static readonly Dictionary<string, DateTime> s_nextAllowed = new();

    public static void Info(string message) => s_logger.Msg(message);
    public static void Warn(string message) => s_logger.Warning(message);
    public static void Error(string message) => s_logger.Error(message);

    public static void ErrorThrottled(string key, string message, double seconds = 10)
    {
        if (Allow(key, seconds)) s_logger.Error(message);
    }

    public static void InfoThrottled(string key, string message, double seconds = 10)
    {
        if (Allow(key, seconds)) s_logger.Msg(message);
    }

    private static bool Allow(string key, double seconds)
    {
        var now = DateTime.UtcNow;
        if (s_nextAllowed.TryGetValue(key, out var next) && now < next) return false;
        s_nextAllowed[key] = now.AddSeconds(seconds);
        return true;
    }
}
