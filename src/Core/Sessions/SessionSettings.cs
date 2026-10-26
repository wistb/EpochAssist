namespace EpochAssist.Core;

// Session history's switch and limits, the sessions section of the General group (Config).
internal static class SessionSettings
{
    public static readonly Section Section = Config.Group.Section("sessions", "Sessions");
    public static readonly Toggle History = new(Section, "SessionHistory", "Keep session history", true)
    {
        Help = "Records zones, XP, gold, drops, deaths, echoes and monolith picks of each play session in UserData/EpochAssist/sessions. Nothing leaves your computer.",
    };
    public static readonly Number MaxCount = new(Section, "SessionMaxCount", "Keep at most (sessions)", 200f, 10f, 1000f)
    {
        Step = 10f, Help = "The oldest sessions are removed first.",
    };
    public static readonly Number MaxTotalMB = new(Section, "SessionMaxTotalMB", "History size limit (MB)", 100f, 10f, 2000f)
    {
        Step = 10f,
    };
    public static readonly Number MaxMB = new(Section, "SessionMaxMB", "One session's limit (MB)", 10f, 1f, 50f)
    {
        Step = 1f, Help = "Past it, the session keeps its totals but no more detail.",
    };

    // Explicit, so every field is set by the time Declare reads the section, before the group registers.
    static SessionSettings() { }

    /// <summary>Declares the section in the General group; Runtime.Init calls it before Config.Init registers it.</summary>
    internal static void Declare()
    {
        _ = Section;
    }
}
