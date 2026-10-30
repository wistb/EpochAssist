namespace EpochAssist.Core;

/// <summary>
/// One row on a menu page. Each kind is drawn, searched and printed by its renderer in the UI layer; a row type with no
/// renderer is a declaration problem. Functions in rows run during the menu's GUI passes: keep them cheap, and safe
/// while the feature isn't running.
/// </summary>
internal abstract class MenuRow
{
    private readonly string? _key;

    /// <summary>Stable within its page; focus and the bridge use it.</summary>
    public string Key
    {
        get => _key ?? DefaultKey;
        init => _key = value;
    }

    protected abstract string DefaultKey { get; }

    public Func<bool>? VisibleWhen { get; init; }
    public Func<bool>? EnabledWhen { get; init; }

    public virtual bool Visible => VisibleWhen?.Invoke() ?? true;
    public bool Enabled => EnabledWhen?.Invoke() ?? true;
}

/// <summary>A setting's row: its control, label, help and visibility all come from the setting.</summary>
internal sealed class SettingRow : MenuRow
{
    public SettingRow(Setting setting, string? hintOverride = null)
    {
        Setting = setting;
        HintOverride = hintOverride;
    }

    public Setting Setting { get; }
    public string? HintOverride { get; }
    /// <summary>The setting's help is the label's and control's tooltip instead of a hint on the row (rows that share
    /// one help, the enemy weights).</summary>
    public bool HelpAsTip { get; init; }
    public string? Hint => HintOverride ?? (HelpAsTip ? null : Setting.Help);
    public string? Tip => HelpAsTip ? Setting.Help : null;
    protected override string DefaultKey => Setting.Id;
    public override bool Visible => Setting.Visible && base.Visible;
}

/// <summary>A small-caps heading with a divider line.</summary>
internal sealed class HeadingRow : MenuRow
{
    public HeadingRow(string text) => Text = text;
    public string Text { get; }
    protected override string DefaultKey => "heading." + MenuEntry.Slug(Text);
}

/// <summary>Muted text across the page.</summary>
internal sealed class HintRow : MenuRow
{
    public HintRow(Func<string> text) => Text = text;
    public Func<string> Text { get; }
    protected override string DefaultKey => "hint";
}

/// <summary>A flat banner with an accent bar, shown while its text isn't null; an optional button beside it, and a
/// second one at the far right (a confirmation's Keep).</summary>
internal sealed class BannerRow : MenuRow
{
    public BannerRow(Func<string?> text, ActionButton? button = null, ActionButton? second = null)
    {
        Text = text;
        Button = button;
        Second = second;
    }

    public Func<string?> Text { get; }
    public ActionButton? Button { get; }
    public ActionButton? Second { get; }
    protected override string DefaultKey => "banner";
}

/// <summary>A button that runs an action by name.</summary>
internal sealed class ActionButton : MenuRow
{
    public ActionButton(string label, string action, bool primary = false)
    {
        Label = label;
        Action = action;
        Primary = primary;
    }

    public string Label { get; }
    public string Action { get; }
    public bool Primary { get; }
    /// <summary>Runs instead of the named action (a list entry's own button); Action is then only its key.</summary>
    public Action? OnClick { get; init; }
    /// <summary>Opens a page outside the game (the browser): drawn with the external link mark.</summary>
    public bool External { get; init; }
    /// <summary>Asks first, in a popup beside the button; it acts through the prompt's Submit, not OnClick.</summary>
    public ButtonPrompt? Prompt { get; init; }
    public string? Tip { get; init; }
    /// <summary>A tip that changes while the menu is open; used instead of Tip when set.</summary>
    public Func<string>? TipText { get; init; }
    protected override string DefaultKey => "button." + Action;
}

/// <summary>
/// What a control asks before it acts, drawn by the UI in a popup beside it: a question, or a one-line field when Input
/// (Initial fills it). Submit gets the field's text (empty for a question) and gives back why it can't be taken, or null
/// once done.
/// </summary>
internal sealed record ButtonPrompt(Func<string> Question, string Ok, Func<string, string?> Submit)
{
    public bool Input { get; init; }
    public Func<string>? Initial { get; init; }
}

/// <summary>A live readout: a label and a value in its state's colour. Shown only while the feature runs.</summary>
internal sealed class LiveText : MenuRow
{
    public LiveText(string label, Func<(string Text, StatusState? State)> value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public Func<(string Text, StatusState? State)> Value { get; }
    /// <summary>Makes the value a link: drawn in blue, lighter on hover, run on click.</summary>
    public Action? OnClick { get; init; }
    public string? Tip { get; init; }
    protected override string DefaultKey => "live." + MenuEntry.Slug(Label);
}

/// <summary>A label, a text in the value column and a small button at its end (a value set elsewhere, with a way there).</summary>
internal sealed class TextButtonRow : MenuRow
{
    public TextButtonRow(string label, Func<string> text, ActionButton button)
    {
        Label = label;
        Text = text;
        Button = button;
    }

    public string Label { get; }
    public Func<string> Text { get; }
    public ActionButton Button { get; }
    /// <summary>A second small button before the text (a stepper's back), or null.</summary>
    public ActionButton? Before { get; init; }
    protected override string DefaultKey => "textbutton." + MenuEntry.Slug(Label);
}

/// <summary>A chip's look: on, paused (on but held) or off. Not a setting type.</summary>
internal enum ChipState { Off, Paused, On }

internal sealed record Chip(string Number, string Label, ChipState State);

/// <summary>Labelled chips across the page (the slot macros). Shown only while the feature runs.</summary>
internal sealed class ChipStrip : MenuRow
{
    public ChipStrip(Func<IReadOnlyList<Chip>> chips) => Chips = chips;
    public Func<IReadOnlyList<Chip>> Chips { get; }
    protected override string DefaultKey => "chips";
}

/// <summary>Several numbers on one row, each label above its field.</summary>
internal sealed class NumberGroup : MenuRow
{
    public NumberGroup(IReadOnlyList<Number> numbers, string? label = null)
    {
        Numbers = numbers;
        Label = label;
    }

    public IReadOnlyList<Number> Numbers { get; }
    public string? Label { get; }
    protected override string DefaultKey => "numbers." + (Label != null ? MenuEntry.Slug(Label) : string.Join("-", Numbers.Select(n => n.Id)));
    public override bool Visible => Numbers.Any(n => n.Visible) && base.Visible;
}

/// <summary>A node of a checkbox tree; Value, when given, is shown at the row's end.</summary>
internal sealed record CheckNode(string Key, string Label, IReadOnlyList<CheckNode> Children, string? Value = null);

/// <summary>
/// Checkbox rows on tree lines. Chosen holds the leaves' keys; checking a parent checks its leaves, and a parent shows
/// mixed while only some are.
/// </summary>
internal sealed class CheckTree : MenuRow
{
    public CheckTree(Func<IReadOnlyList<CheckNode>> nodes, ISet<string> chosen, string key)
    {
        Nodes = nodes;
        Chosen = chosen;
        Key = key;
    }

    public Func<IReadOnlyList<CheckNode>> Nodes { get; }
    public ISet<string> Chosen { get; }
    protected override string DefaultKey => "checks";
}

/// <summary>A named block drawn by the menu itself inside a page.</summary>
internal sealed class BlockRow : MenuRow
{
    public BlockRow(string block, object? data = null)
    {
        Block = block;
        Data = data;
    }

    public string Block { get; }
    public object? Data { get; }
    protected override string DefaultKey => "block." + Block;
}

/// <summary>A dropdown bound to a value rather than a stored setting (list entries, runtime data).</summary>
internal sealed class ValueChoice : MenuRow
{
    public ValueChoice(string label, Func<int> get, Action<int> set, Func<IReadOnlyList<string>> options)
    {
        Label = label;
        Get = get;
        Set = set;
        Options = options;
    }

    public string Label { get; }
    public Func<int> Get { get; }
    public Action<int> Set { get; }
    public Func<IReadOnlyList<string>> Options { get; }
    /// <summary>Headings in the dropdown: each group starts at option index First.</summary>
    public Func<(string Label, int First)[]?>? Groups { get; init; }
    /// <summary>The text shown when Get is outside the options ("Buff #81").</summary>
    public Func<string?>? Shown { get; init; }
    /// <summary>The width inline, when the options' own would be too wide for the line.</summary>
    public float? Width { get; init; }
    /// <summary>A colour swatch before each option (null for none), shown in the list and on the closed control.</summary>
    public Func<IReadOnlyList<UnityEngine.Color?>>? Swatches { get; init; }
    /// <summary>An example before each option, drawn by the owner at its width (an icon in its colour), or null.</summary>
    public (float Width, Action<int, UnityEngine.Rect> Draw)? Sample { get; init; }
    protected override string DefaultKey => "choice." + MenuEntry.Slug(Label);
}

/// <summary>A key bound to a value: captured like a hold binding (modifiers first, mouse 3–7 allowed), "Not set" when
/// empty, with the hotkey layer's conflict warnings as a glyph's tip.</summary>
internal sealed class ValueKey : MenuRow
{
    public ValueKey(string label, Func<string> get, Action<string> set)
    {
        Label = label;
        Get = get;
        Set = set;
    }

    public string Label { get; }
    /// <summary>Chord text, empty for none.</summary>
    public Func<string> Get { get; }
    public Action<string> Set { get; }
    /// <summary>Warnings for the chord (conflicts with other bindings).</summary>
    public Func<IReadOnlyList<string>>? Warnings { get; init; }
    protected override string DefaultKey => "key." + MenuEntry.Slug(Label);
}

/// <summary>A live value inline: text in its state's colour and shape; a null state is the error look.</summary>
internal sealed class ValueReadout : MenuRow
{
    public ValueReadout(string label, Func<(string Text, StatusState? State)> value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public Func<(string Text, StatusState? State)> Value { get; }
    /// <summary>Its words for the bridge ("pass", "unknown").</summary>
    public Func<string>? Word { get; init; }
    /// <summary>A longer text as its tip (why it's unknown).</summary>
    public Func<string?>? Tip { get; init; }
    protected override string DefaultKey => "readout." + MenuEntry.Slug(Label);
}

/// <summary>Muted text inline in a list entry (an invalid entry's stored text and reason).</summary>
internal sealed class ValueText : MenuRow
{
    public ValueText(string label, Func<string> text)
    {
        Label = label;
        Text = text;
    }

    public string Label { get; }
    public Func<string> Text { get; }
    /// <summary>A fixed width, so the controls after it line up from entry to entry; null fits the text.</summary>
    public float? Width { get; init; }
    protected override string DefaultKey => "text." + MenuEntry.Slug(Label);
}

/// <summary>A number field bound to a value; clamped and snapped like a Number setting.</summary>
internal sealed class ValueNumber : MenuRow
{
    public ValueNumber(string label, Func<float> get, Action<float> set, float min, float max, float step = 1f, string unit = "")
    {
        Label = label;
        Get = get;
        Set = set;
        Min = min;
        Max = max;
        Step = step;
        Unit = unit;
    }

    public string Label { get; }
    public Func<float> Get { get; }
    public Action<float> Set { get; }
    public float Min { get; }
    public float Max { get; }
    public float Step { get; }
    public string Unit { get; }
    /// <summary>Drawn as a slider instead of a number field.</summary>
    public bool Slider { get; init; }
    /// <summary>Muted words beside the field, as a setting's help shows on its row; null for none.</summary>
    public string? Hint { get; init; }
    /// <summary>A longer explanation as the label's and field's tooltip; null for none.</summary>
    public string? Tip { get; init; }
    protected override string DefaultKey => "number." + MenuEntry.Slug(Label);

    public float Snap(float value) => Math.Clamp(Step > 0f ? MathF.Round(value / Step) * Step : value, Min, Max);

    public string Format(float value) => value.ToString(Step >= 1f ? "F0" : Step >= 0.1f ? "F1" : "F2") + Unit;
}

/// <summary>A switch bound to a value.</summary>
internal sealed class ValueToggle : MenuRow
{
    public ValueToggle(string label, Func<bool> get, Action<bool> set)
    {
        Label = label;
        Get = get;
        Set = set;
    }

    public string Label { get; }
    public Func<bool> Get { get; }
    public Action<bool> Set { get; }
    /// <summary>Muted words beside the switch, as a setting's help shows on its row; null for none.</summary>
    public string? Hint { get; init; }
    /// <summary>A longer explanation as the label's and switch's tooltip; null for none.</summary>
    public string? Tip { get; init; }
    protected override string DefaultKey => "toggle." + MenuEntry.Slug(Label);
}

/// <summary>
/// An ordered list of entries, each one line of inline controls (RowsOf), with up, down and remove buttons and an add
/// button last. Entries are runtime values, edited through Value rows; whoever owns the list stores it.
/// </summary>
internal sealed class ListRow : MenuRow
{
    public ListRow(string label, Func<IReadOnlyList<object>> items, Func<object, IReadOnlyList<MenuRow>> rowsOf, string addLabel = "Add")
    {
        Label = label;
        Items = items;
        RowsOf = rowsOf;
        AddLabel = addLabel;
    }

    public string Label { get; }
    public Func<IReadOnlyList<object>> Items { get; }
    public Func<object, IReadOnlyList<MenuRow>> RowsOf { get; }
    public string AddLabel { get; }
    public Action? Add { get; init; }
    /// <summary>When given, the add button opens a dropdown of these (grouped by AddGroups) and AddPicked gets the index.</summary>
    public Func<IReadOnlyList<string>>? AddChoices { get; init; }
    public Func<(string Label, int First)[]?>? AddGroups { get; init; }
    public Action<int>? AddPicked { get; init; }
    /// <summary>Why nothing can be added now (the add button greys with it as its tip), or null.</summary>
    public Func<string?>? AddBlocked { get; init; }
    public Action<int>? Remove { get; init; }
    /// <summary>When it gives a prompt, an entry's remove asks first beside its button; the prompt then removes.</summary>
    public Func<int, ButtonPrompt?>? RemovePrompt { get; init; }
    /// <summary>Why an entry can't be removed (its remove button greys with it as its tip), or null.</summary>
    public Func<int, string?>? RemoveBlocked { get; init; }
    public Action<int, int>? Move { get; init; }
    /// <summary>A second line under an entry while it returns rows (its details unfolded), else null.</summary>
    public Func<object, IReadOnlyList<MenuRow>?>? Details { get; init; }
    protected override string DefaultKey => "list." + MenuEntry.Slug(Label);
}

/// <summary>A row holding other rows (cards): search, reset and the bridge look inside it.</summary>
internal interface IRowContainer
{
    IEnumerable<MenuRow> Children { get; }
}

/// <summary>A titled card: a title, a short description and its rows, drawn inside a framed panel.</summary>
internal sealed class CardRow : MenuRow, IRowContainer
{
    public CardRow(string title, string? description, params MenuRow[] rows)
    {
        Title = title;
        Description = description;
        Rows = rows;
    }

    public string Title { get; }
    public string? Description { get; }
    public IReadOnlyList<MenuRow> Rows { get; }
    public IEnumerable<MenuRow> Children => Rows;
    protected override string DefaultKey => "card." + MenuEntry.Slug(Title);
}

/// <summary>Cards side by side, equal widths and the tallest's height (one column on a narrow page).</summary>
internal sealed class CardColumns : MenuRow, IRowContainer
{
    public CardColumns(params CardRow[] cards) => Cards = cards;

    public IReadOnlyList<CardRow> Cards { get; }
    public IEnumerable<MenuRow> Children => Cards;
    protected override string DefaultKey => "cards." + string.Join("-", Cards.Select(c => MenuEntry.Slug(c.Title)));
}
