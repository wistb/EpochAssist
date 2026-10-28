namespace EpochAssist.Core;

// Session history's switch and limits, the sessions section of the General group (Config), and which figures the
// dashboard puts first (its own section, shown as chips on the Current session page and as switches on History).
internal static class SessionSettings
{
    public static readonly Section Section = Config.Group.Section("sessions", "Sessions");
    public static readonly Toggle History = new(Section, "SessionHistory", "Keep session history", true)
    {
        Help = "Records zones, XP, gold, drops, deaths, echoes, monolith picks, level-ups and faction favour, reputation and memory amber of each play session in UserData/EpochAssist/sessions. Nothing leaves your computer.",
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

    public static readonly Section Dashboard = Config.Group.Section("sessiondash", "Session dashboard");
    public static readonly Toggle FocusFavour = Focus("Favour", true);
    public static readonly Toggle FocusReputation = Focus("Reputation", true);
    public static readonly Toggle FocusAmber = Focus("Amber", true, "Memory amber");
    public static readonly Toggle FocusEchoes = Focus("Echoes", true);
    public static readonly Toggle FocusLevel = Focus("Level", true);
    public static readonly Toggle FocusDrops = Focus("Drops", true);
    public static readonly Toggle FocusXp = Focus("Xp", true, "XP");
    public static readonly Toggle FocusGold = Focus("Gold", true);
    public static readonly Toggle FocusDeaths = Focus("Deaths", false);

    // No help of their own: the History page explains them once above the switches.
    private static Toggle Focus(string id, bool on, string? label = null) => new(Dashboard, "SessionFocus" + id, label ?? id, on);

    // Explicit, so every field is set by the time Declare reads the section, before the group registers.
    static SessionSettings() { }

    /// <summary>Declares the section in the General group; Runtime.Init calls it before Config.Init registers it.</summary>
    internal static void Declare()
    {
        _ = Section;
        _ = Dashboard;
    }
}
