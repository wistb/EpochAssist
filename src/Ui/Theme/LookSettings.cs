using EpochAssist.Core;

namespace EpochAssist.Ui;

// The menu's look, the appearance section of the General group (Config).
internal static class LookSettings
{
    private static readonly Section Look = Config.Group.Section("appearance", "Appearance");
    public static readonly Pick Theme = new(Look, "Theme", "Theme", "Carbonfox")
    {
        Help = "Built-in looks, plus your own from UserData/EpochAssist/themes.",
    };
    // The accents offered: Carbonfox's own (nightfox's palette), then Catppuccin Mocha's (catppuccin.com/palette) and
    // Tokyo Night's (github.com/folke/tokyonight.nvim), which suit the dark themes. Each set is a heading in the list.
    private static readonly (string Name, string Hex)[] s_swatches =
    {
        ("Teal", "#3DDBD9"), ("Cyan", "#33B1FF"), ("Blue", "#78A9FF"), ("Light blue", "#82CFFF"), ("Purple", "#BE95FF"),
        ("Magenta", "#FF7EB6"), ("Pink", "#EE5396"), ("Orange", "#FF832B"), ("Green", "#25BE6A"),
        ("Rosewater", "#F5E0DC"), ("Flamingo", "#F2CDCD"), ("Pink", "#F5C2E7"), ("Mauve", "#CBA6F7"), ("Red", "#F38BA8"),
        ("Maroon", "#EBA0AC"), ("Peach", "#FAB387"), ("Yellow", "#F9E2AF"), ("Green", "#A6E3A1"), ("Teal", "#94E2D5"),
        ("Sky", "#89DCEB"), ("Sapphire", "#74C7EC"), ("Blue", "#89B4FA"), ("Lavender", "#B4BEFE"),
        ("Blue", "#7AA2F7"), ("Cyan", "#7DCFFF"), ("Aqua", "#2AC3DE"), ("Teal", "#73DACA"), ("Green", "#9ECE6A"),
        ("Yellow", "#E0AF68"), ("Orange", "#FF9E64"), ("Red", "#F7768E"), ("Magenta", "#BB9AF7"),
    };
    private static readonly (string Label, int First)[] s_swatchGroups = { ("Carbonfox", 0), ("Catppuccin", 9), ("Tokyo Night", 23) };

    public static readonly ColorSetting Accent = new(Look, "Accent", "Accent")
    {
        EmptyLabel = "Theme", Swatches = s_swatches, Groups = s_swatchGroups,
        Help = "Switches, check boxes, main buttons, the focus ring and banners.",
    };
    public static readonly ColorSetting AccentSecondary = new(Look, "AccentSecondary", "Secondary accent")
    {
        EmptyLabel = "Theme", Swatches = s_swatches, Groups = s_swatchGroups,
        Help = "Where you are: the selected row in the tree, the highlighted option in lists, search matches and slot chips that are on.",
    };
    public static readonly ColorSetting AccentTertiary = new(Look, "AccentTertiary", "Tertiary accent")
    {
        EmptyLabel = "Theme", Swatches = s_swatches, Groups = s_swatchGroups,
        Help = "Accent text: key boxes, slot numbers and the current option in lists.",
    };
    public static readonly Choice<int> Font = new(Look, "GameFont", "Font", 1, (0, "Unity"), (1, "Pretendard"), (2, "Noto Sans"))
    {
        Help = "Fonts the game ships with.",
    };
    public static readonly Choice<int> FontWeight = new(Look, "FontWeight", "Text", 0, (0, "Regular"), (1, "Bold"))
    {
        Help = "Weight of labels, buttons and values. Hints stay regular.",
    };
    // 50% by default (maintainer): the themes' own radii read too round at the game's scale.
    public static readonly Number Corners = new(Look, "Corners", "Corners", 0.5f, 0f, 2f)
    {
        Step = 0.05f, Percent = true, ApplyOnRelease = true,
        Help = "How round corners are: 100% is the theme's own, 0% square.",
    };
    public static readonly Number UiScale = new(Look, "UiScale", "UI scale", 1.25f, 0.75f, 2f)
    {
        Step = 0.05f, Unit = "x", ApplyOnRelease = true, Help = "Size of the menu and the mini HUD.",
    };
    public static readonly Number MenuOpacity = new(Look, "MenuOpacity", "Background", 1f, 0.2f, 1f)
    {
        Step = 0.01f, Percent = true, Help = "Menu background opacity; text and switches stay solid.",
    };
    public static readonly Toggle Animations = new(Look, "Animations", "Animations", true)
    {
        Help = "Motion in the menu, such as the tree opening and pages fading in. Off, everything changes at once.",
    };
    public static readonly Number AnimationSpeed = new(Look, "AnimationSpeed", "Animation speed", 1f, 0.5f, 3f)
    {
        Step = 0.1f, Unit = "x", VisibleWhen = () => Animations.Value, ShownWhen = "Animations is on", Help = "Higher is faster.",
    };

    // Explicit, so every field is set by the time Declare reads the section, before the group registers.
    static LookSettings() { }

    /// <summary>Declares the section in the General group; Runtime.Init calls it before Config.Init registers it.</summary>
    internal static void Declare()
    {
        _ = Look;
    }
}
