namespace EpochAssist.Core;

/// <summary>Every registered group, for the menu and the HUD's quick toggles.</summary>
internal static class SettingsRegistry
{
    private static readonly List<SettingsGroup> s_groups = new();

    public static IReadOnlyList<SettingsGroup> Groups => s_groups;

    public static IEnumerable<Toggle> QuickToggles =>
        s_groups.SelectMany(g => g.All).OfType<Toggle>().Where(t => t.QuickLabel != null);

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
        foreach (var toggle in group.All.OfType<Toggle>().Where(t => t.QuickLabel != null))
        {
            Actions.Register("settings", ToggleAction(toggle), toggle.QuickLabel!, () => toggle.Value = !toggle.Value, () => toggle.Value);
        }
    }

    public static string ResetAction(Section section) => $"settings.reset.{Key(section.Group)}.{Slug(section.Id)}";

    public static string ToggleAction(Toggle toggle) => $"setting.toggle.{Key(toggle.Section.Group)}.{Slug(toggle.Id)}";

    private static string Key(SettingsGroup group) => Slug(group.ActionKey.Length > 0 ? group.ActionKey : group.Id);

    private static string Slug(string text) => new(text.ToLowerInvariant().Where(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')).ToArray());
}
