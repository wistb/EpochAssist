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
    public string? Hint => HintOverride ?? Setting.Help;
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

/// <summary>A flat banner with an accent bar, shown while its text isn't null; an optional button beside it.</summary>
internal sealed class BannerRow : MenuRow
{
    public BannerRow(Func<string?> text, ActionButton? button = null)
    {
        Text = text;
        Button = button;
    }

    public Func<string?> Text { get; }
    public ActionButton? Button { get; }
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
    protected override string DefaultKey => "button." + Action;
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
    protected override string DefaultKey => "live." + MenuEntry.Slug(Label);
}

/// <summary>A chip's look: on, paused (on but held) or off. Not a setting type.</summary>
internal enum ChipState { Off, Paused, On }

internal sealed record Chip(string Number, string Label, ChipState State);

/// <summary>Labelled chips across the page (Autocast's slots). Shown only while the feature runs.</summary>
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
    protected override string DefaultKey => "choice." + MenuEntry.Slug(Label);
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
    public Action<int>? Remove { get; init; }
    public Action<int, int>? Move { get; init; }
    protected override string DefaultKey => "list." + MenuEntry.Slug(Label);
}
