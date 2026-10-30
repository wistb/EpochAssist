namespace EpochAssist.Core;

// Session history's switch and limits, the sessions section of the General group (Config), which figures the
// dashboard puts first (its own section, shown as chips on the Current session page and as switches on History), and
// the figures on the mini HUD (their own section, the Session tab's Mini HUD page).
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

    // The current session's figures on the mini HUD (Session tab › Mini HUD). Figure choices are stored as numbers:
    // 0 hidden, 1 total, 2 per hour, 3 both; figures without a rate offer hidden and shown (1).
    public static readonly Section Hud = Config.Group.Section("sessionhud", "Mini HUD");
    // No help: the HUD previews behind the menu, so each choice shows what it does. What doesn't apply to the look is
    // hidden instead of explained.
    public static readonly Toggle HudShow = new(Hud, "SessionHudShow", "Show on the mini HUD", false);
    public static readonly Choice<int> HudLook = new(Hud, "SessionHudLook", "Look", 0, (0, "Card"), (1, "In the pill"), (2, "Tiles"));
    public static readonly Choice<int> HudRate = new(Hud, "SessionHudRate", "Per hour over", 0, (0, "Whole session"), (1, "Last 10 minutes"), (2, "Time out of town"));
    public static readonly Choice<int> HudSize = new(Hud, "SessionHudSize", "Text size", 2, (0, "XS"), (1, "S"), (2, "M"), (3, "L")) { VisibleWhen = NotInPill };
    public static readonly Choice<int> HudScale = new(Hud, "SessionHudScale", "Card size", 2, (0, "XS"), (1, "S"), (2, "M"), (3, "L")) { VisibleWhen = NotInPill };
    public static readonly Toggle HudFixed = new(Hud, "SessionHudFixed", "Fixed-width digits", false) { VisibleWhen = NotInPill };
    public static readonly Toggle HudGraphs = new(Hud, "SessionHudGraphs", "Graphs", true) { VisibleWhen = NotInPill };
    public static readonly Choice<int> HudLine = new(Hud, "SessionHudLine", "Graph line", 1, (0, "Thin"), (1, "Normal"), (2, "Thick"), (3, "Bold"))
    {
        VisibleWhen = () => NotInPill() && HudGraphs.Value,
    };
    public static readonly Toggle HudLabels = new(Hud, "SessionHudLabels", "Labels", true);

    // Text size, card size, digits and graphs belong to the card and tiles; the pill has its own text and no graphs.
    private static bool NotInPill() => HudLook.Value != 1;

    // Each figure's colour on the mini HUD and the Session tab: empty is the game's own (the dashboard's role colours).
    public static readonly Section Colours = Config.Group.Section("sessioncolours", "Colours");
    public static readonly Toggle HudColours = new(Colours, "SessionHudColours", "Colours", true);
    public static readonly ColorSetting ColourXp = Colour("Xp", "XP", "xp");
    public static readonly ColorSetting ColourGold = Colour("Gold", "Gold", "gold");
    public static readonly ColorSetting ColourDrops = Colour("Drops", "Drops", "drops");
    public static readonly ColorSetting ColourEchoes = Colour("Echoes", "Echoes", "echoes");
    public static readonly ColorSetting ColourFavour = Colour("Favour", "Favour", "favour");
    public static readonly ColorSetting ColourReputation = Colour("Reputation", "Reputation", "rep");
    public static readonly ColorSetting ColourAmber = Colour("Amber", "Memory amber", "amber");
    public static readonly ColorSetting ColourDeaths = Colour("Deaths", "Deaths", "deaths");

    /// <summary>The colour a figure (the dashboard's metric key) is set to, or null for its game colour.</summary>
    public static ColorSetting? ColourOf(string key) => key switch
    {
        "xp" => ColourXp, "gold" => ColourGold, "drops" => ColourDrops, "echoes" => ColourEchoes, "favour" => ColourFavour,
        "rep" => ColourReputation, "amber" => ColourAmber, "deaths" => ColourDeaths, _ => null,
    };

    /// <summary>Set by the UI: a figure's game colour, for its colour's default swatch.</summary>
    public static Func<string, UnityEngine.Color?>? GameColour { get; set; }

    private static ColorSetting Colour(string id, string label, string key) => new(Colours, "SessionColour" + id, label)
    {
        EmptyLabel = "Game", EmptyColour = () => GameColour?.Invoke(key), VisibleWhen = () => HudColours.Value,
    };

    public const int Hidden = 0, Total = 1, PerHour = 2, Both = 3;
    private const string Figures = "Figures";
    public static readonly Choice<int> HudTime = Shown("Time", "Session time", true);
    public static readonly Choice<int> HudXp = Rated("Xp", "XP", PerHour);
    public static readonly Choice<int> HudGold = Rated("Gold", "Gold", PerHour);
    public static readonly Choice<int> HudDrops = Rated("Drops", "Drops", Hidden);
    public static readonly Choice<int> HudEchoes = Rated("Echoes", "Echoes", Total);
    public static readonly Choice<int> HudLevel = Shown("Level", "Level", false);
    public static readonly Choice<int> HudFavour = Rated("Favour", "Favour", Hidden);
    public static readonly Choice<int> HudReputation = Rated("Reputation", "Reputation", Hidden);
    public static readonly Choice<int> HudAmber = Rated("Amber", "Memory amber", Hidden);
    public static readonly Choice<int> HudDeaths = Rated("Deaths", "Deaths", Total);

    private static Choice<int> Rated(string id, string label, int shown) =>
        new(Hud, "SessionHud" + id, label, shown, (Hidden, "Hidden"), (Total, "Total"), (PerHour, "Per hour"), (Both, "Both")) { Fold = Figures };
    private static Choice<int> Shown(string id, string label, bool shown) =>
        new(Hud, "SessionHud" + id, label, shown ? Total : Hidden, (Hidden, "Hidden"), (Total, "Shown")) { Fold = Figures };

    // Explicit, so every field is set by the time Declare reads the section, before the group registers.
    static SessionSettings() { }

    /// <summary>Declares the section in the General group; Runtime.Init calls it before Config.Init registers it.</summary>
    internal static void Declare()
    {
        _ = Section;
        _ = Dashboard;
        _ = Hud;
        _ = Colours;
    }
}
