namespace EpochAssist.Core;

/// <summary>How an entry's page area is laid out: the usual pages, or the macro library's own layout.</summary>
internal enum MenuLayout { Pages, Library }

/// <summary>
/// A line of the library tree: a set (depth 0) or a macro under it (depth 1), with its tick box (null for none), its
/// label and state text, and the state's shape. Keys are "set/macro" paths ("slots/2", "s1/m3").
/// </summary>
internal sealed record LibraryLine(string Key, int Depth, string Label, string State, StatusState Shape, bool? Ticked, bool Parent);

/// <summary>
/// An editor that replaces the library's content: a title, header buttons and its rows, drawn as a page's rows are.
/// Key changes when another item is opened, so scroll and focus start again.
/// </summary>
internal sealed class LibraryEditor
{
    public LibraryEditor(string key, Func<string> title, Func<IReadOnlyList<MenuRow>> rows)
    {
        Key = key;
        Title = title;
        Rows = rows;
    }

    public string Key { get; }
    public Func<string> Title { get; }
    /// <summary>Header buttons, from the left (Undo, Test fire, Close).</summary>
    public List<ActionButton> Buttons { get; } = new();
    /// <summary>A line under the header (the last test fire's result), or null.</summary>
    public Func<string?>? Note { get; init; }
    /// <summary>A short key-cap badge before the title (a slot's number), or null.</summary>
    public Func<string?>? Badge { get; init; }
    /// <summary>Where the edited item lives, added to the breadcrumb ("Slots", a set's name), or null.</summary>
    public Func<string?>? Place { get; init; }
    public Func<IReadOnlyList<MenuRow>> Rows { get; }
}

/// <summary>
/// The macro library's layout as data (contracts/menu.md): a toolbar (class dropdown, buttons, a status tag), the
/// library tree on the left with tick boxes and states, the selection's actions on the right, footer switches and
/// buttons at the bottom; an open editor replaces the tree and actions. The UI draws it; the feature never draws.
/// </summary>
internal sealed class LibraryView
{
    public Func<IReadOnlyList<string>> Classes { get; init; } = () => Array.Empty<string>();
    public Func<int> Class { get; init; } = () => 0;
    public Action<int> SetClass { get; init; } = _ => { };
    public List<ActionButton> Toolbar { get; } = new();
    public Func<PageStatus>? Status { get; init; }
    /// <summary>A line under the toolbar while it returns text (a paste's result).</summary>
    public Func<string?>? Banner { get; init; }

    public Func<IReadOnlyList<LibraryLine>> Lines { get; init; } = () => Array.Empty<LibraryLine>();
    /// <summary>The tick box of a line was clicked (or Space on it).</summary>
    public Action<LibraryLine> Tick { get; init; } = _ => { };
    /// <summary>The selected line's key, or null.</summary>
    public string? Selected { get; set; }
    /// <summary>Enter on a line, or a double click: its first action (usually Edit).</summary>
    public Action<LibraryLine>? Open { get; init; }
    /// <summary>The actions for the selected line (empty with none).</summary>
    public Func<LibraryLine?, IReadOnlyList<ActionButton>> Actions { get; init; } = _ => Array.Empty<ActionButton>();
    /// <summary>A hint under the actions (why one is greyed, the selection's details), or null.</summary>
    public Func<LibraryLine?, string?>? ActionHint { get; init; }

    /// <summary>The line being renamed (its label is a text field), or null; RenameText is the field's text.</summary>
    public string? Renaming { get; set; }
    public string RenameText { get; set; } = string.Empty;
    /// <summary>Enter in the rename field: the new name for the line.</summary>
    public Action<LibraryLine, string>? Rename { get; init; }

    /// <summary>Starts renaming a line, with its label as the text.</summary>
    public void BeginRename(LibraryLine line)
    {
        Renaming = line.Key;
        RenameText = line.Label;
        Selected = line.Key;
    }

    /// <summary>Switches at the bottom left (Run macros, Debug conditions, Debug ranges).</summary>
    public List<Toggle> Footer { get; } = new();
    /// <summary>Buttons at the bottom right (Undo).</summary>
    public List<ActionButton> FooterButtons { get; } = new();

    /// <summary>The open editor, or null for the library.</summary>
    public Func<LibraryEditor?> Editor { get; init; } = () => null;
    /// <summary>Ctrl+Z on the tab (undo); null when it has none.</summary>
    public Action? Undo { get; init; }
    /// <summary>Esc while an editor is open closes it; true when it did.</summary>
    public Func<bool>? Back { get; init; }

    /// <summary>The line with this key in the current lines, or null.</summary>
    public LibraryLine? Line(string? key) => key == null ? null : Lines().FirstOrDefault(l => l.Key == key);
}
