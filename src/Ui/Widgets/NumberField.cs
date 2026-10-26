using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>What a number field needs to know about its value: range, step, how to show and read it.</summary>
internal sealed record NumberSpec(float Min, float Max, float Step, Func<float, string> Format, Func<float, float> Snap, Func<string, float?> Parse)
{
    public static NumberSpec Of(Number number) => new(number.Min, number.Max, number.Step, number.Format, number.Snap, text => ParseNumber(text, number.Percent));

    public static NumberSpec Of(ValueNumber number) => new(number.Min, number.Max, number.Step, number.Format, number.Snap, text => ParseNumber(text, false));

    // What a player types: digits with an optional sign, decimal point or comma, and the unit or a % left on.
    private static float? ParseNumber(string text, bool percent)
    {
        string digits = new(text.Trim().Replace(',', '.').Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        if (!float.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)) return null;
        return percent ? value / 100f : value;
    }
}

/// <summary>
/// A compact number field (90 px): the value with ▲ ▼ steppers. A press on a stepper steps once, and holding repeats
/// after 400 ms every 80 ms; a click on the value opens it for typing (Enter or leaving commits, snapped and clamped;
/// Esc cancels). Left and Right step it when focused. The wheel never changes it. Values that move the UI itself
/// (ApplyOnRelease) apply when the stepper is let go.
/// </summary>
internal static class NumberField
{
    public const float Width = 90f;
    private const float FirstRepeat = 0.4f, Repeat = 0.08f, StepperWidth = 14f;

    // A held stepper: whose, which way, and when it steps next.
    private static string? s_held;
    private static int s_heldDirection;
    private static float s_nextStep;
    private static float s_heldValue;
    private static Action<float>? s_heldSet;
    private static NumberSpec? s_heldSpec;
    // ApplyOnRelease values while their stepper is held.
    private static readonly Dictionary<Number, float> s_pending = new();

    /// <summary>Applies held ApplyOnRelease values once nothing is held. Call before drawing.</summary>
    public static void CommitPending()
    {
        if (s_pending.Count == 0 || s_held != null) return;
        foreach (var (number, value) in s_pending) number.Value = value;
        s_pending.Clear();
    }

    public static void Reset()
    {
        s_held = null;
        s_heldSet = null;
        s_pending.Clear();
    }

    /// <summary>A Number setting's field.</summary>
    public static void Draw(Rect r, string key, Number number, string? tip = null)
    {
        float shown = s_pending.TryGetValue(number, out float held) ? held : number.Value;
        Draw(r, key, shown, NumberSpec.Of(number), value =>
        {
            if (number.ApplyOnRelease && s_held != null) s_pending[number] = value;
            else number.Value = value;
        }, tip);
    }

    public static void Draw(Rect r, string key, float value, NumberSpec spec, Action<float> set, string? tip = null)
    {
        Event e = Event.current;
        var up = new Rect(r.xMax - StepperWidth - 2f, r.y + 1f, StepperWidth, r.height / 2f - 1f);
        var down = new Rect(up.x, r.center.y, StepperWidth, r.height / 2f - 1f);
        var text = new Rect(r.x, r.y, r.width - StepperWidth - 2f, r.height);
        bool editing = Popups.Current is NumberEditPopup edit && edit.Key == key;

        if (GUI.enabled && e.type == EventType.MouseDown && e.button == 0)
        {
            if (up.Contains(e.mousePosition) || down.Contains(e.mousePosition))
            {
                int direction = up.Contains(e.mousePosition) ? 1 : -1;
                s_held = key;
                s_heldDirection = direction;
                s_heldValue = value;
                s_heldSet = set;
                s_heldSpec = spec;
                s_nextStep = Time.unscaledTime + FirstRepeat;
                Step(direction);
                Focus.Take(key);
                e.Use();
            }
            else if (text.Contains(e.mousePosition))
            {
                Focus.Take(key);
                Popups.Open(new NumberEditPopup(key, Popups.ToWindow(r), value, spec, set));
                e.Use();
            }
        }
        if (s_held == key)
        {
            // Let go anywhere (the window sees the release while the press began on it).
            if (e.type == EventType.MouseUp || (e.type == EventType.Repaint && !Input.GetMouseButton(0)))
            {
                s_held = null;
                s_heldSet = null;
                if (e.type == EventType.MouseUp) e.Use();
            }
            else if (e.type == EventType.Repaint && Time.unscaledTime >= s_nextStep)
            {
                s_nextStep = Time.unscaledTime + Repeat;
                Step(s_heldDirection);
            }
        }

        bool activated = Focus.Item(key, r, out int adjust);
        if (adjust != 0) set(spec.Snap(value + adjust * spec.Step));
        if (activated && !editing) Popups.Open(new NumberEditPopup(key, Popups.ToWindow(r), value, spec, set));
        Popups.Tip(r, tip, key);

        if (e.type != EventType.Repaint) return;
        Controls.FieldFrame(r, editing);
        if (editing) return;
        GUIStyle style = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        Widgets.Paint(text, Controls.Fit(spec.Format(value), style, text.width - style.padding.horizontal), style);
        if (!Widgets.Themed) return;
        bool hover = GUI.enabled && r.Contains(e.mousePosition);
        Color arrow = hover ? Theme.Text : Theme.Dim;
        Theme.Tinted(new Rect(Mathf.Round(up.center.x - 4f), Mathf.Round(up.yMax - 6f), 8f, 5f), Theme.StepUp, s_held == key && s_heldDirection > 0 ? Theme.AccentText : arrow);
        Theme.Tinted(new Rect(Mathf.Round(down.center.x - 4f), Mathf.Round(down.y + 1f), 8f, 5f), Theme.StepDown, s_held == key && s_heldDirection < 0 ? Theme.AccentText : arrow);
    }

    private static void Step(int direction)
    {
        if (s_heldSpec == null || s_heldSet == null) return;
        float next = s_heldSpec.Snap(s_heldValue + direction * s_heldSpec.Step);
        if (next == s_heldValue) return;
        s_heldValue = next;
        s_heldSet(next);
    }
}

/// <summary>A number field open for typing, drawn over the field so its text field never shifts the page's controls.</summary>
internal sealed class NumberEditPopup : IPopup
{
    private const string Control = "EpochAssistNumber";
    private readonly NumberSpec _spec;
    private readonly Action<float> _set;
    private string _text;
    private bool _focused;

    public NumberEditPopup(string key, Rect field, float value, NumberSpec spec, Action<float> set)
    {
        Key = key;
        Rect = field;
        _spec = spec;
        _set = set;
        _text = value.ToString(spec.Step >= 1f ? "0" : spec.Step >= 0.1f ? "0.0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
        // A percent shows as typed in percent.
        if (spec.Format(value).EndsWith("%")) _text = (value * 100f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    public string Key { get; }
    public Rect Rect { get; }

    public void HandleEvent(Event e) { }

    public bool HandleKey(Event e)
    {
        if (e.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return false;
        Popups.Close(commit: true);
        return true;
    }

    // Enter commits; moving on (Tab, Up, Down) commits and goes on.
    public bool HandleNav(MenuNav nav)
    {
        if (nav.Activate)
        {
            Popups.Close(commit: true);
            return false;
        }
        return nav.Move != 0;
    }

    public void Closed(bool commit)
    {
        if (GUI.GetNameOfFocusedControl() == Control) GUIUtility.keyboardControl = 0;
        if (!commit || _spec.Parse(_text) is not float value) return;
        _set(_spec.Snap(value));
    }

    public void Draw()
    {
        GUI.SetNextControlName(Control);
        _text = GUI.TextField(Rect, _text, 12, Widgets.Style(() => Theme.TreeFilter, GUI.skin.textField));
        if (!_focused && Event.current.type == EventType.Repaint)
        {
            _focused = true;
            GUI.FocusControl(Control);
        }
    }
}
