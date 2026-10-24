using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

// Draws declared settings: one control per setting kind, sections as cards, search results.
internal static class SettingsView
{
    /// <summary>Width rows may use; the caller sets it to the page width, cards take their padding off it.</summary>
    public static float RowWidth = 380f;
    // A card's padding on both sides, which rows inside it lose.
    private static float CardPadding => Themed ? Theme.CardInset : 28f;

    private static readonly HashSet<string> s_openFolds = new();
    // ApplyOnRelease values while their slider is held.
    private static readonly Dictionary<Number, float> s_pending = new();

    /// <summary>Applies held ApplyOnRelease values once no control is being dragged. Call before drawing.</summary>
    public static void CommitPending()
    {
        if (s_pending.Count == 0 || GUIUtility.hotControl != 0) return;
        foreach (var (number, value) in s_pending) number.Value = value;
        s_pending.Clear();
    }

    /// <summary>
    /// A section's settings as a card titled with the section, with a Reset button when anything differs. Extra, when
    /// given, draws more inside the card after the rows.
    /// </summary>
    public static void Card(Section section, string? title = null, Action? extra = null)
    {
        if (BeginCard((title ?? section.Title).ToUpperInvariant(), canReset: !section.IsDefault))
            Actions.Run(SettingsRegistry.ResetAction(section), "settings card reset");
        float width = RowWidth;
        RowWidth -= CardPadding;
        Rows(section.Settings);
        extra?.Invoke();
        RowWidth = width;
        EndCard();
    }

    /// <summary>The visible settings in order; settings sharing a fold sit under one collapsible header.</summary>
    public static void Rows(IEnumerable<Setting> settings)
    {
        string? fold = null;
        bool foldOpen = true;
        foreach (var setting in settings)
        {
            if (!setting.Visible) continue;
            if (setting.Fold != fold)
            {
                fold = setting.Fold;
                foldOpen = fold == null || FoldHeader(setting.Section.Group.Id + "/" + fold, fold);
            }
            if (foldOpen) Row(setting);
        }
    }

    public static void Row(Setting setting)
    {
        switch (setting)
        {
            case Toggle toggle:
                toggle.Value = Switch(toggle.Label, toggle.Value);
                break;
            case Number number:
                Slider(number);
                break;
            case Key key:
                KeyRow(key);
                break;
            case Hotkey hotkey:
                HotkeyRow(hotkey);
                break;
            case Choice<int> choice:
                Buttons(choice);
                break;
            case Choice<KeyCode> choice:
                Buttons(choice);
                break;
            case Pick pick:
                PickButtons(pick);
                break;
            case ColorSetting color:
                ColorPicker.Row(color);
                break;
        }
        if (setting.Help != null) Hint(setting.Help);
    }

    /// <summary>Search results across every group, labelled with where each setting lives.</summary>
    public static void SearchResults(string query)
    {
        int shown = 0;
        Section? last = null;
        float width = RowWidth;
        RowWidth -= CardPadding;
        foreach (var setting in SettingsRegistry.Search(query))
        {
            if (setting.Section != last)
            {
                if (last != null) EndCard();
                last = setting.Section;
                BeginCard($"{last.Group.Name} · {last.Title}".ToUpperInvariant());
            }
            Row(setting);
            shown++;
        }
        if (last != null) EndCard();
        RowWidth = width;
        if (shown == 0) Hint($"No settings match \"{query}\".");
    }

    private static void Slider(Number number)
    {
        float current = s_pending.TryGetValue(number, out float held) ? held : number.Value;
        float value = number.Snap(SliderRow(number.Label, current, number.Min, number.Max, number.Format(current), number.Default, number.Step));
        if (value == current) return;
        if (number.ApplyOnRelease) s_pending[number] = value;
        else number.Value = value;
    }

    private static void Buttons<T>(Choice<T> choice) where T : notnull
    {
        int index = Buttons(choice.Label, choice.Options.Select(o => o.Label).ToList(), choice.Index, choice.Cells);
        if (index != choice.Index) choice.Index = index;
    }

    // One button per option; a stored name that isn't among them selects none. Notes (file problems) follow in the
    // paused colour.
    private static void PickButtons(Pick pick)
    {
        var options = pick.Options?.Invoke() ?? new[] { pick.Value };
        int current = options.ToList().FindIndex(o => o.Equals(pick.Value, StringComparison.OrdinalIgnoreCase));
        int index = Buttons(pick.Label, options, current);
        if (index != current && index >= 0) pick.Value = options[index];
        foreach (string note in pick.Notes?.Invoke() ?? Enumerable.Empty<string>()) Hint(Colored(note, Theme.Pause));
    }

    // Buttons get fixed widths so a row never runs past its card: up to three beside the label when their text fits,
    // otherwise in a grid below it (two per row for four, three per row beyond, or the given cells three to a row).
    // Returns the selected index.
    private static int Buttons(string rowLabel, IReadOnlyList<string> labels, int index, int[]? cells = null)
    {
        const float gap = 4f;
        GUIStyle label = Style(() => Theme.RowLabel, GUI.skin.label);
        GUIStyle segment = Style(() => Theme.Segment, GUI.skin.button);
        int count = labels.Count;
        if (count == 0) return index;
        float widest = labels.Max(o => segment.CalcSize(new GUIContent(o)).x);
        bool beside = cells == null && count <= 3 && Mathf.Floor((RowWidth - LabelWidth - gap * count) / count) >= widest;
        int perRow = cells != null ? 3 : count <= 3 ? count : count == 4 ? 2 : 3;
        cells ??= Enumerable.Range(0, count).ToArray();
        float available = beside ? RowWidth - LabelWidth : RowWidth;
        float width = Mathf.Floor((available - gap * perRow) / perRow);

        if (!beside) GUILayout.Label(rowLabel, label, GUILayout.Height(26f));
        int selected = index;
        for (int start = 0; start < cells.Length; start += perRow)
        {
            GUILayout.BeginHorizontal();
            if (beside) GUILayout.Label(rowLabel, label, GUILayout.Width(LabelWidth), GUILayout.Height(30f));
            for (int cell = start; cell < Math.Min(start + perRow, cells.Length); cell++)
            {
                int i = cells[cell];
                if (i < 0 || i >= count)
                {
                    GUILayout.Space(width + segment.margin.horizontal);
                    continue;
                }
                bool clicked = GUILayout.Toggle(index == i, labels[i], segment, GUILayout.Width(width)) && index != i;
                // Enter picks the focused option; Left and Right step to the one before or after the current choice.
                if (Focus.Item($"choice.{rowLabel}.{labels[i]}", null, out int adjust) || clicked) selected = i;
                if (adjust != 0) selected = Math.Clamp((index < 0 ? i : index) + adjust, 0, count - 1);
            }
            GUILayout.EndHorizontal();
        }
        return selected;
    }

    private static bool FoldHeader(string key, string label)
    {
        bool open = s_openFolds.Contains(key);
        GUILayout.Space(4f);
        bool next = GUILayout.Toggle(open, open ? "Hide " + label.ToLowerInvariant() : label,
            Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false));
        if (Focus.Item("fold." + key)) next = !open;
        if (next != open)
        {
            if (next) s_openFolds.Add(key);
            else s_openFolds.Remove(key);
        }
        return next;
    }
}
