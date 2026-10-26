namespace EpochAssist.Core;

/// <summary>
/// A HUD quick toggle: a switch with a label and an action (setting.toggle.[group].[id]) the HUD, the footer, hotkeys
/// and the console run. A Toggle with a QuickLabel is one; a setting may offer others (a condition list's named entries).
/// </summary>
internal interface IQuickSwitch
{
    string Label { get; }
    string? Tip { get; }
    bool Value { get; }
    void Set(bool on);
    string ActionId { get; }
}

/// <summary>Every registered group, for the menu and the HUD's quick toggles.</summary>
internal static class SettingsRegistry
{
    private static readonly List<SettingsGroup> s_groups = new();

    public static IReadOnlyList<SettingsGroup> Groups => s_groups;

    /// <summary>Every quick switch, in declaration order; a setting's own switches come at its place.</summary>
    public static IEnumerable<IQuickSwitch> QuickToggles => s_groups.SelectMany(g => g.All).SelectMany(s => s.QuickSwitches);

    // Each group's sections get a reset action and its quick toggles a toggle action, owned by the settings layer.
    internal static void Add(SettingsGroup group)
    {
        if (s_groups.Contains(group)) return;
        s_groups.Add(group);
        foreach (var section in group.Sections)
        {
            Actions.Register("settings", ResetAction(section), $"Reset {section.Title}", () =>
            {
                section.Reset();
                Toasts.Show("settings", $"{section.Title} reset to defaults.", ToastKind.Info);
            });
        }
        foreach (var quick in group.All.SelectMany(s => s.QuickSwitches))
        {
            Actions.Register("settings", quick.ActionId, quick.Label, () => quick.Set(!quick.Value), () => quick.Value);
        }
    }

    public static string ResetAction(Section section) => $"settings.reset.{Key(section.Group)}.{Slug(section.Id)}";

    public static string ToggleAction(Toggle toggle) => ToggleAction(toggle.Section.Group, toggle.Id);

    /// <summary>A quick switch's action id: setting.toggle.[group].[id].</summary>
    public static string ToggleAction(SettingsGroup group, string id) => $"setting.toggle.{Key(group)}.{Slug(id)}";

    private static string Key(SettingsGroup group) => Slug(group.ActionKey.Length > 0 ? group.ActionKey : group.Id);

    private static string Slug(string text) => new(text.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());
}
