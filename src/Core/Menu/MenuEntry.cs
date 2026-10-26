namespace EpochAssist.Core;

/// <summary>The menu's top tabs, by id. Their order and titles are the menu's; a feature names one in its entry.</summary>
internal static class MenuCategory
{
    public const string Combat = "combat";
    public const string Overlays = "overlays";
    public const string Map = "map";
    public const string Visuals = "visuals";
    public const string Stats = "stats";
    public const string Settings = "settings";
    /// <summary>Dev builds only.</summary>
    public const string Dev = "dev";
}

/// <summary>
/// A feature's place in the menu: its tab, its node in the tree, the pages under it and the rows on them. Data only: the
/// menu reads it and draws it, in any state the feature is in. Declare it once as a static field and return it from
/// <see cref="Feature.Menu"/>.
/// </summary>
internal sealed class MenuEntry
{
    public MenuEntry(string category, int order = 0)
    {
        Category = category;
        Order = order;
    }

    public string Category { get; }
    /// <summary>Place among the tab's nodes, lower first; ties keep registration order.</summary>
    public int Order { get; }
    /// <summary>The tree node's text; null for the feature's name.</summary>
    public string? Title { get; init; }
    /// <summary>The feature's master switch, shown by the node's dot; it must have a QuickLabel (it runs that action).</summary>
    public Toggle? Switch { get; init; }
    /// <summary>When false, the pages' rows draw dimmed and take no input.</summary>
    public Func<bool>? ContentEnabled { get; init; }
    /// <summary>Rows above every page of this entry.</summary>
    public List<MenuRow> Top { get; } = new();
    /// <summary>Tree-only groups of items, shown before the pages.</summary>
    public List<TreeGroup> Groups { get; } = new();
    public List<MenuPage> Pages { get; } = new();

    /// <summary>A path part: lower case, spaces as dashes, other punctuation dropped.</summary>
    public static string Slug(string text)
    {
        var chars = new List<char>(text.Length);
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) chars.Add(c);
            else if ((c == ' ' || c == '-') && chars.Count > 0 && chars[^1] != '-') chars.Add('-');
        }
        if (chars.Count > 0 && chars[^1] == '-') chars.RemoveAt(chars.Count - 1);
        return new string(chars.ToArray());
    }
}

/// <summary>
/// One page under a tree node. With a section and no rows, the rows are the section's settings in declared order, each
/// fold a heading; explicit rows replace that.
/// </summary>
internal sealed class MenuPage
{
    public MenuPage(Section section)
    {
        Section = section;
        Title = section.Title;
    }

    public MenuPage(string title) => Title = title;

    public string Title { get; init; }
    public Section? Section { get; }
    public List<MenuRow> Rows { get; } = new();
    /// <summary>A switch in the title row (the page's own on/off); it must have a QuickLabel.</summary>
    public Toggle? Switch { get; init; }
    /// <summary>The title row's tag; null shows the feature's status.</summary>
    public Func<PageStatus>? Status { get; init; }
    /// <summary>A banner under the title row while it returns text.</summary>
    public Func<string?>? Banner { get; init; }
    /// <summary>A named block that fills the page under the title row.</summary>
    public string? Block { get; init; }
    /// <summary>Buttons in the title row, before Reset.</summary>
    public List<ActionButton> Actions { get; } = new();
    /// <summary>Whether the title row offers Reset; null for "when the page has settings".</summary>
    public bool? Resettable { get; init; }
    /// <summary>False: not listed under its node in the tree; reached from a tree item that opens it (Autocast's slot
    /// pages), search, the bridge and page keys.</summary>
    public bool InTree { get; init; } = true;
}

/// <summary>A tree row that expands to items and opens no page of its own (Autocast's "Slots").</summary>
internal sealed record TreeGroup(string Title, Func<IReadOnlyList<TreeItem>> Items);

/// <summary>An item under a tree group: a number in the accent colour when on, a label and a state dot. Opens a page, or the entry's first.</summary>
internal sealed record TreeItem(string? Number, string Label, StatusState State, MenuPage? Opens = null);

/// <summary>The title row's tag: a state shape and its text.</summary>
internal readonly record struct PageStatus(StatusState State, string Text);
