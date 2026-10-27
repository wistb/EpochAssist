using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// Settings as rows: the control comes from the setting's kind, the label, help and range from its declaration. Also
// the value-bound kinds list entries use, with the same controls.
internal static class SettingRows
{
    public static void Register()
    {
        RowKinds.Register<SettingRow>(new SettingRenderer());
        RowKinds.Register<ValueChoice>(new ValueChoiceRenderer());
        RowKinds.Register<ValueNumber>(new ValueNumberRenderer());
        RowKinds.Register<ValueToggle>(new ValueToggleRenderer());
        RowKinds.Register<ValueKey>(new ValueKeyRenderer());
        RowKinds.Register<ValueReadout>(new ValueReadoutRenderer());
        RowKinds.Register<ValueText>(new ValueTextRenderer());
    }

    /// <summary>A setting's focus key: its group and id, the same on every page and in search results.</summary>
    public static string KeyOf(Setting setting) => $"{setting.Section.Group.Id}.{setting.Id}";

    /// <summary>A row's focus key on its page.</summary>
    public static string FocusKey(MenuRow row) => row is SettingRow s ? KeyOf(s.Setting) : row.Key;

    private static bool HotkeysRunning => FeatureHost.Find("hotkeys")?.State == FeatureState.Running;

    /// <summary>A Pick's options, with the stored name kept even when it isn't listed.</summary>
    private static (IReadOnlyList<string> Options, int Index) PickOptions(Pick pick)
    {
        var options = Safe.Call(() => pick.Options?.Invoke(), null, pick.Id + ".options") ?? new[] { pick.Value };
        int index = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].Equals(pick.Value, StringComparison.OrdinalIgnoreCase)) index = i;
        }
        return (options, index);
    }

    private static IReadOnlyList<string> Warnings(Setting setting) => setting switch
    {
        Pick pick => Safe.Call(() => pick.Notes?.Invoke()?.ToList(), null, pick.Id + ".notes") ?? (IReadOnlyList<string>)Array.Empty<string>(),
        Hotkey hotkey => HotkeyConflicts.For(hotkey),
        _ => Array.Empty<string>(),
    };

    private static List<string> ChoiceLabels<T>(Choice<T> choice) where T : notnull => choice.Options.Select(o => o.Label).ToList();

    private static IReadOnlyList<string> OptionLabels(Setting setting) => setting switch
    {
        Choice<int> c => ChoiceLabels(c),
        Choice<KeyCode> c => ChoiceLabels(c),
        Choice<string> c => ChoiceLabels(c),
        Pick p => PickOptions(p).Options,
        ColorSetting c => ColorPicker.Options(c),
        _ => Array.Empty<string>(),
    };

    // Change, and Clear (when it can be cleared) or Reset (when it can't and was changed).
    private static (string Text, bool Capturing, string? Second) KeyParts(Setting setting)
    {
        bool capturing = Hotkeys.Capturing == setting;
        string text = setting switch
        {
            Key key => capturing ? "Press a key..." : key.Value == KeyCode.None ? "Not set" : key.Value.ToString(),
            Hotkey hotkey => capturing ? (hotkey.Kind == HotkeyKind.Hold ? "Press keys or a mouse button..." : "Press keys...") : hotkey.Display,
            _ => string.Empty,
        };
        bool clearable = setting is Key { Clearable: true } k ? k.Value != KeyCode.None : setting is Hotkey { Clearable: true } h && h.Value.Length > 0;
        bool resettable = setting is Key { Clearable: false } || setting is Hotkey { Clearable: false };
        string? second = capturing ? null : clearable ? "Clear" : resettable && !setting.IsDefault ? "Reset" : null;
        return (text, capturing, second);
    }

    /// <summary>The control's width for a setting (key rows: box, Change and the second button).</summary>
    public static float ControlWidth(Setting setting) => setting switch
    {
        Toggle => Controls.SwitchWidth,
        Number { Slider: true } => Slider.Width,
        Number => NumberField.Width,
        Choice<int> or Choice<KeyCode> or Choice<string> or Pick => Dropdown.WidthFor(OptionLabels(setting)),
        ColorSetting color => ColorPicker.WidthFor(color),
        Key or Hotkey => KeyWidth(setting),
        _ => 120f,
    };

    private static float KeyWidth(Setting setting)
    {
        var (text, capturing, second) = KeyParts(setting);
        float width = Controls.KeyBoxWidth(text) + 6f + Controls.ButtonWidth(capturing ? "Cancel" : "Change");
        return second != null ? width + 6f + Controls.ButtonWidth(second) : width;
    }

    private static string? HintOf(SettingRow row)
    {
        var warnings = Warnings(row.Setting);
        if (warnings.Count == 0) return row.Hint;
        string warned = string.Join("\n", warnings.Select(w => Widgets.Colored(w, Theme.Pause)));
        return string.IsNullOrEmpty(row.Hint) ? warned : row.Hint + "\n" + warned;
    }

    /// <summary>The setting's value as the menu shows it.</summary>
    public static string ValueText(Setting setting) => setting switch
    {
        Toggle t => t.Value ? "on" : "off",
        Number n => n.Format(n.Value),
        Choice<int> c => c.ValueLabel,
        Choice<KeyCode> c => c.ValueLabel,
        Choice<string> c => c.ValueLabel,
        Pick p => PickOptions(p).Index >= 0 ? p.Value : "missing: " + p.Value,
        ColorSetting c => c.Value.Length > 0 ? c.Value : c.EmptyLabel,
        Key or Hotkey => KeyParts(setting).Text,
        _ => string.Empty,
    };

    public static string KindText(Setting setting) => setting switch
    {
        Toggle => "switch",
        Number { Slider: true } => "slider",
        Number => "number",
        Choice<int> or Choice<KeyCode> or Choice<string> or Pick => "dropdown",
        ColorSetting => "colour",
        Key or Hotkey => "key",
        _ => "setting",
    };

    /// <summary>A switch flips through its quick-toggle action when it has one, as the footer and HUD do.</summary>
    public static void Flip(Toggle toggle, string trigger)
    {
        if (toggle.QuickLabel != null && !Actions.Run(SettingsRegistry.ToggleAction(toggle), trigger).StartsWith("error")) return;
        toggle.Value = !toggle.Value;
    }

    /// <summary>A quick switch flips through its action, falling back to setting it directly.</summary>
    public static void Flip(IQuickSwitch quick, string trigger)
    {
        if (!Actions.Run(quick.ActionId, trigger).StartsWith("error")) return;
        quick.Set(!quick.Value);
    }

    /// <summary>One setting's control in a rect.</summary>
    public static void Control(Setting setting, Rect r, string key, string? tip)
    {
        switch (setting)
        {
            case Toggle toggle:
                if (Controls.Switch(new Rect(r.x, r.y, Controls.SwitchWidth, r.height), toggle.Value, key, tip) != toggle.Value) Flip(toggle, "menu");
                break;
            case Number { Slider: true } number:
                Slider.Draw(r, key, number, tip);
                break;
            case Number number:
                NumberField.Draw(r, key, number, tip);
                break;
            case Choice<int> choice:
                Dropdown.Draw(r, key, ChoiceLabels(choice), choice.Index, i => choice.Index = i, choice.Groups, tip: tip);
                break;
            case Choice<KeyCode> choice:
                Dropdown.Draw(r, key, ChoiceLabels(choice), choice.Index, i => choice.Index = i, choice.Groups, tip: tip);
                break;
            case Choice<string> choice:
                Dropdown.Draw(r, key, ChoiceLabels(choice), choice.Index, i => choice.Index = i, choice.Groups, tip: tip);
                break;
            case Pick pick:
                var (options, index) = PickOptions(pick);
                Dropdown.Draw(r, key, options, index, i =>
                {
                    if (i >= 0 && i < options.Count) pick.Value = options[i];
                }, shown: index >= 0 ? null : "missing: " + pick.Value, tip: tip);
                break;
            case ColorSetting color:
                ColorPicker.Draw(r, key, color, tip);
                break;
            case Key or Hotkey:
                KeyControl(setting, r, key);
                break;
        }
    }

    private static void KeyControl(Setting setting, Rect r, string key)
    {
        var (text, capturing, second) = KeyParts(setting);
        float box = Controls.KeyBoxWidth(text);
        Controls.KeyBox(new Rect(r.x, r.y, box, r.height), text, capturing);
        float x = r.x + box + 6f;
        string change = capturing ? "Cancel" : "Change";
        float width = Controls.ButtonWidth(change);
        // Capture is read by the hotkey layer, so it needs that layer running.
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && (capturing || setting is not Hotkey || HotkeysRunning);
        if (Controls.Button(new Rect(x, r.y, width, r.height), change, key + ".change"))
        {
            if (capturing) Hotkeys.CancelCapture();
            else if (setting is Key k) Hotkeys.BeginCapture(k);
            else if (setting is Hotkey h) Hotkeys.BeginCapture(h);
        }
        GUI.enabled = wasEnabled;
        if (second == null) return;
        x += width + 6f;
        if (!Controls.Button(new Rect(x, r.y, Controls.ButtonWidth(second), r.height), second, key + "." + second.ToLowerInvariant())) return;
        if (second == "Reset") setting.Reset();
        else if (setting is Key clearKey) clearKey.Value = KeyCode.None;
        else if (setting is Hotkey clearHotkey) clearHotkey.Value = string.Empty;
    }

    private sealed class SettingRenderer : RowRenderer<SettingRow>
    {
        protected override float Height(SettingRow row, float width, RowContext ctx) =>
            ctx.Inline ? RowLayout.ControlHeight : RowLayout.Height(width, ControlWidth(row.Setting), HintOf(row));

        protected override float InlineWidth(SettingRow row) => ControlWidth(row.Setting);

        protected override void Draw(SettingRow row, Rect rect, RowContext ctx)
        {
            Setting setting = row.Setting;
            string key = KeyOf(setting);
            if (ctx.Inline)
            {
                Control(setting, new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight), key, setting.Label);
                return;
            }
            string? hint = HintOf(row);
            var parts = RowLayout.Lay(rect, ControlWidth(setting), hint);
            // The label of a switch flips it too, as the whole row did.
            if (setting is Toggle toggle && GUI.Button(parts.Label, GUIContent.none, GUIStyle.none)) Flip(toggle, "menu");
            RowLayout.Label(parts.Label, setting.Label, ctx, row.Hint);
            Control(setting, parts.Control, key, row.Hint);
            if (!setting.IsDefault && setting is not (Key or Hotkey) && Controls.ResetMark(parts.Reset, key + ".reset")) setting.Reset();
            RowLayout.Hint(parts.Hint, hint);
        }

        protected override IEnumerable<string> Search(SettingRow row)
        {
            Setting setting = row.Setting;
            yield return setting.Label;
            if (setting.QuickLabel != null) yield return setting.QuickLabel;
            if (row.Hint != null) yield return row.Hint;
            foreach (string option in OptionLabels(setting)) yield return option;
        }

        protected override RowText Bridge(SettingRow row, RowContext ctx) =>
            new(KindText(row.Setting), row.Setting.Label, ValueText(row.Setting), ctx.Enabled && Safe.Enabled(row), row.Setting.IsDefault, row.Hint)
            {
                Warnings = Warnings(row.Setting).Select(RowText.StripTags).ToList(),
            };
    }

    private sealed class ValueChoiceRenderer : RowRenderer<ValueChoice>
    {
        private static IReadOnlyList<string> Options(ValueChoice row) => Safe.Call(row.Options, Array.Empty<string>(), row.Key + ".options");

        protected override float Height(ValueChoice row, float width, RowContext ctx) =>
            ctx.Inline ? RowLayout.ControlHeight : RowLayout.Height(width, InlineWidth(row), null);

        protected override float InlineWidth(ValueChoice row) => row.Width ?? Dropdown.WidthFor(Options(row));

        protected override void Draw(ValueChoice row, Rect rect, RowContext ctx)
        {
            var options = Options(row);
            Rect control = ctx.Inline ? new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight) : RowLayout.Lay(rect, InlineWidth(row), null).Control;
            if (!ctx.Inline) RowLayout.Label(RowLayout.Lay(rect, InlineWidth(row), null).Label, row.Label, ctx, null);
            int current = Safe.Call(row.Get, -1, row.Key + ".get");
            var groups = row.Groups == null ? null : Safe.Call(row.Groups, null, row.Key + ".groups");
            string? shown = current >= 0 || row.Shown == null ? null : Safe.Call(row.Shown, null, row.Key + ".shown");
            Dropdown.Draw(control, ctx.Prefix + row.Key, options, current, i => Safe.Call(() => { row.Set(i); return true; }, false, row.Key + ".set"),
                groups, shown, tip: row.Label);
        }

        protected override IEnumerable<string> Search(ValueChoice row) => Options(row).Prepend(row.Label);

        protected override RowText Bridge(ValueChoice row, RowContext ctx)
        {
            var options = Options(row);
            int current = Safe.Call(row.Get, -1, row.Key + ".get");
            string value = current >= 0 && current < options.Count ? options[current] : row.Shown != null ? Safe.Call(row.Shown, null, row.Key + ".shown") ?? string.Empty : string.Empty;
            return new RowText("dropdown", row.Label, value, ctx.Enabled && Safe.Enabled(row));
        }
    }

    // A key bound to a value: its box, then Change (or Cancel while capturing); a warning glyph when it overlaps another key.
    private sealed class ValueKeyRenderer : RowRenderer<ValueKey>
    {
        private static bool Capturing(ValueKey row, RowContext ctx) => Hotkeys.CapturingTarget?.Label == CaptureLabel(row, ctx);

        private static string CaptureLabel(ValueKey row, RowContext ctx) => ctx.Prefix + row.Key;

        private static string Text(ValueKey row, bool capturing)
        {
            if (capturing) return "Press keys or a mouse button...";
            string chord = Safe.Call(row.Get, string.Empty, row.Key + ".get");
            return Chord.TryParse(chord, HotkeyKind.Hold, out var parsed) && !parsed.IsEmpty ? parsed.Display : "Not set";
        }

        protected override float Height(ValueKey row, float width, RowContext ctx) => RowLayout.ControlHeight;

        protected override float InlineWidth(ValueKey row) => Controls.KeyBoxWidth(Text(row, false)) + 6f + Controls.ButtonWidth("Change") + 26f;

        protected override void Draw(ValueKey row, Rect rect, RowContext ctx)
        {
            bool capturing = Capturing(row, ctx);
            string text = Text(row, capturing);
            float box = Controls.KeyBoxWidth(text);
            Controls.KeyBox(new Rect(rect.x, rect.y, box, RowLayout.ControlHeight), text, capturing);
            float x = rect.x + box + 6f;
            string change = capturing ? "Cancel" : "Change";
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && (capturing || HotkeysRunning);
            if (Controls.Button(new Rect(x, rect.y, Controls.ButtonWidth(change), RowLayout.ControlHeight), change, ctx.Prefix + row.Key + ".change", tip: row.Label))
            {
                if (capturing) Hotkeys.CancelCapture();
                else Hotkeys.BeginCapture(new CaptureTarget(CaptureLabel(row, ctx), HotkeyKind.Hold, chord => Safe.Call(() => { row.Set(chord); return true; }, false, row.Key + ".set")));
            }
            GUI.enabled = wasEnabled;
            x += Controls.ButtonWidth(change) + 6f;
            var warnings = row.Warnings == null ? Array.Empty<string>() : Safe.Call(row.Warnings, Array.Empty<string>(), row.Key + ".warnings");
            if (warnings.Count == 0) return;
            var glyph = new Rect(x, Mathf.Round(rect.y + RowLayout.ControlHeight / 2f - 8f), 16f, 16f);
            if (Event.current.type == EventType.Repaint) Widgets.Paint(glyph, Widgets.Themed ? Widgets.Colored("!", Theme.Pause) : "!", Widgets.Style(() => Theme.Label, GUI.skin.label));
            Popups.Tip(glyph, string.Join("\n", warnings), ctx.Prefix + row.Key + ".warning");
        }

        protected override IEnumerable<string> Search(ValueKey row) => new[] { row.Label };

        protected override RowText Bridge(ValueKey row, RowContext ctx)
        {
            var warnings = row.Warnings == null ? Array.Empty<string>() : Safe.Call(row.Warnings, Array.Empty<string>(), row.Key + ".warnings");
            string chord = Safe.Call(row.Get, string.Empty, row.Key + ".get");
            return new RowText("key", row.Label, chord.Length > 0 ? chord : "none", ctx.Enabled && Safe.Enabled(row)) { Warnings = warnings.ToList() };
        }
    }

    // A live value inline, with its state's shape before the text; the error look when the state is null.
    private sealed class ValueReadoutRenderer : RowRenderer<ValueReadout>
    {
        private static (string Text, StatusState? State) Value(ValueReadout row) => Safe.Call(row.Value, (string.Empty, (StatusState?)null), row.Key);

        protected override float Height(ValueReadout row, float width, RowContext ctx) => RowLayout.ControlHeight;

        protected override float InlineWidth(ValueReadout row) =>
            Mathf.Clamp(TextCache.Width(Value(row).Text, Widgets.Style(() => Theme.Label, GUI.skin.label)) + 18f, 48f, 130f);

        protected override void Draw(ValueReadout row, Rect rect, RowContext ctx)
        {
            var (text, state) = Value(row);
            var r = new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight);
            string? tip = row.Tip == null ? null : Safe.Call(row.Tip, null, row.Key + ".tip");
            Popups.Tip(r, tip, ctx.Prefix + row.Key);
            if (Event.current.type != EventType.Repaint) return;
            Color color = state is { } s ? Theme.StateColor(s) : Theme.Error;
            float x = r.x;
            if (Widgets.Themed)
            {
                Menu.DrawState(new Rect(x, Mathf.Round(r.center.y - 4f), 8f, 8f), state ?? StatusState.Holding, color);
                x += 14f;
            }
            GUIStyle style = Widgets.Style(() => Theme.Label, GUI.skin.label);
            var area = new Rect(x, r.y, r.xMax - x, r.height);
            string fitted = Controls.Fit(text, style, area.width);
            Widgets.Paint(area, Widgets.Themed ? Widgets.Colored(fitted, Theme.LightText(color)) : fitted, style);
        }

        protected override IEnumerable<string> Search(ValueReadout row) => Array.Empty<string>();

        protected override RowText Bridge(ValueReadout row, RowContext ctx)
        {
            string word = row.Word == null ? string.Empty : Safe.Call(row.Word, string.Empty, row.Key + ".word");
            return new RowText("readout", row.Label, $"{RowText.Quote(Value(row).Text)} {word}".TrimEnd());
        }
    }

    // Muted text inline.
    private sealed class ValueTextRenderer : RowRenderer<ValueText>
    {
        private static string Text(ValueText row) => Safe.Call(row.Text, string.Empty, row.Key);

        protected override float Height(ValueText row, float width, RowContext ctx) => RowLayout.ControlHeight;

        private static GUIStyle Style => Widgets.Style(() => Theme.Label, GUI.skin.label);

        protected override float InlineWidth(ValueText row) => row.Width ?? Mathf.Clamp(TextCache.Width(Text(row), Style) + 8f, 40f, 600f);

        protected override void Draw(ValueText row, Rect rect, RowContext ctx)
        {
            if (Event.current.type != EventType.Repaint) return;
            var r = new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight);
            string text = Controls.Fit(Text(row), Style, r.width);
            Widgets.Paint(r, Widgets.Themed ? Widgets.Colored(text, Theme.Dim) : text, Style);
        }

        protected override IEnumerable<string> Search(ValueText row) => new[] { Text(row) };

        protected override RowText Bridge(ValueText row, RowContext ctx) => new("text", row.Label, Text(row));
    }

    private sealed class ValueNumberRenderer : RowRenderer<ValueNumber>
    {
        protected override float Height(ValueNumber row, float width, RowContext ctx) =>
            ctx.Inline ? RowLayout.ControlHeight : RowLayout.Height(width, NumberField.Width, null);

        protected override float InlineWidth(ValueNumber row) => NumberField.Width;

        protected override void Draw(ValueNumber row, Rect rect, RowContext ctx)
        {
            var parts = RowLayout.Lay(rect, NumberField.Width, null);
            Rect control = ctx.Inline ? new Rect(rect.x, rect.y, rect.width, RowLayout.ControlHeight) : parts.Control;
            if (!ctx.Inline) RowLayout.Label(parts.Label, row.Label, ctx, null);
            float value = Safe.Call(row.Get, row.Min, row.Key + ".get");
            NumberField.Draw(control, ctx.Prefix + row.Key, value, NumberSpec.Of(row), v => Safe.Call(() => { row.Set(v); return true; }, false, row.Key + ".set"), row.Label);
        }

        protected override IEnumerable<string> Search(ValueNumber row) => new[] { row.Label };

        protected override RowText Bridge(ValueNumber row, RowContext ctx) =>
            new("number", row.Label, row.Format(Safe.Call(row.Get, row.Min, row.Key + ".get")), ctx.Enabled && Safe.Enabled(row));
    }

    private sealed class ValueToggleRenderer : RowRenderer<ValueToggle>
    {
        protected override float Height(ValueToggle row, float width, RowContext ctx) =>
            ctx.Inline ? RowLayout.ControlHeight : RowLayout.Height(width, Controls.SwitchWidth, null);

        protected override float InlineWidth(ValueToggle row) => Controls.SwitchWidth;

        protected override void Draw(ValueToggle row, Rect rect, RowContext ctx)
        {
            var parts = RowLayout.Lay(rect, Controls.SwitchWidth, null);
            Rect control = ctx.Inline ? new Rect(rect.x, rect.y, Controls.SwitchWidth, RowLayout.ControlHeight) : parts.Control;
            if (!ctx.Inline) RowLayout.Label(parts.Label, row.Label, ctx, null);
            bool value = Safe.Call(row.Get, false, row.Key + ".get");
            if (Controls.Switch(control, value, ctx.Prefix + row.Key, row.Label) != value) Safe.Call(() => { row.Set(!value); return true; }, false, row.Key + ".set");
        }

        protected override IEnumerable<string> Search(ValueToggle row) => new[] { row.Label };

        protected override RowText Bridge(ValueToggle row, RowContext ctx) =>
            new("switch", row.Label, Safe.Call(row.Get, false, row.Key + ".get") ? "on" : "off", ctx.Enabled && Safe.Enabled(row));
    }
}
