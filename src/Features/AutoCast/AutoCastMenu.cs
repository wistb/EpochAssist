using EpochAssist.Core;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

// Autocast's place in the menu, as data: the slot chips and toggle hint above every page, the slots in the tree, and a
// page per settings section. The menu draws it.
internal static class AutoCastMenu
{
    public static readonly MenuEntry Entry = new(MenuCategory.Combat, order: 10)
    {
        Switch = S.Enabled,
        ContentEnabled = () => S.Enabled.Value,
        Top =
        {
            new ChipStrip(Chips),
            new HintRow(ToggleHint),
        },
        Groups = { new TreeGroup("Slots", SlotItems) },
        Pages =
        {
            new MenuPage(S.Controls),
            new MenuPage(S.Pausing),
            new MenuPage(S.Targeting),
            GatePage(),
        },
    };

    // The gate's own switch sits in the title row, so its rows are the section's others, then what it sees now.
    private static MenuPage GatePage()
    {
        var page = new MenuPage(S.Gate)
        {
            Switch = S.RequireEnemies, Status = GateStatus,
            Banner = () => S.RequireEnemies.Value ? S.RequireEnemies.Label + "." : $"{S.RequireEnemies.Label} is off. The switch above turns it on.",
        };
        foreach (var setting in S.Gate.Settings.Where(s => s != S.RequireEnemies && s.Fold == null)) page.Rows.Add(new SettingRow(setting));
        var weights = S.Gate.Settings.Where(s => s.Fold != null).OfType<Number>().ToList();
        page.Rows.Add(new HeadingRow(weights[0].Fold!) { VisibleWhen = () => S.RequireEnemies.Value });
        page.Rows.Add(new NumberGroup(weights, weights[0].Fold));
        page.Rows.Add(new HeadingRow("Now") { VisibleWhen = () => S.RequireEnemies.Value });
        page.Rows.Add(new LiveText("In range", GateReadout) { VisibleWhen = () => S.RequireEnemies.Value });
        return page;
    }

    // The dispatcher's status while autocast runs, otherwise off.
    private static FeatureStatus Status => FeatureHost.Get<AutoCastFeature>()?.Status ?? FeatureStatus.Off(string.Empty);

    private static IReadOnlyList<Chip> Chips()
    {
        FeatureStatus status = Status;
        var chips = new Chip[AutoCast.Slots];
        for (int i = 0; i < chips.Length; i++)
        {
            bool toggled = status.Slots?[i] ?? false;
            ChipState state = !toggled ? ChipState.Off : status.State == StatusState.Active ? ChipState.On : ChipState.Paused;
            chips[i] = new Chip((i + 1).ToString(), AutoCast.SlotAbilityName(i) ?? "-", state);
        }
        return chips;
    }

    // Why autocast waits or holds is in the page's status tag.
    private static string ToggleHint() => $"Hold {AutoCast.ModifierKeyName} and press a skill's key to toggle its slot.";

    private static IReadOnlyList<TreeItem> SlotItems()
    {
        FeatureStatus status = Status;
        var items = new TreeItem[AutoCast.Slots];
        for (int i = 0; i < items.Length; i++)
        {
            bool toggled = status.Slots?[i] ?? false;
            items[i] = new TreeItem((i + 1).ToString(), AutoCast.SlotAbilityName(i) ?? "-", toggled ? StatusState.Active : StatusState.Off);
        }
        return items;
    }

    private static PageStatus GateStatus() => !S.RequireEnemies.Value ? new PageStatus(StatusState.Off, "off")
        : AutoCast.EnemyGateOpen ? new PageStatus(StatusState.Active, "casting") : new PageStatus(StatusState.Paused, "holding");

    private static (string, StatusState?) GateReadout() => (
        $"{AutoCast.EnemiesInRange} enemies, weight {AutoCast.EnemyWeightInRange:F0} · {(AutoCast.EnemyGateOpen ? "casting" : "holding")}",
        AutoCast.EnemyGateOpen ? StatusState.Active : StatusState.Paused);
}
