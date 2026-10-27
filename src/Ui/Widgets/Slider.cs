using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// A slider for a Number setting that asks for one (Number.Slider): a track filled in the accent up to the value, a knob,
/// and the value beside it. A press on the track jumps there and dragging follows the mouse, snapped to the step; Left
/// and Right step it when focused. The wheel never changes it.
/// </summary>
internal static class Slider
{
    public const float Width = 220f;
    private const float ValueWidth = 56f, Knob = 12f, Track = 4f;

    // The slider being dragged, by key.
    private static string? s_dragging;

    public static void Reset() => s_dragging = null;

    /// <summary>A Number setting's slider.</summary>
    public static void Draw(Rect r, string key, Number number, string? tip = null) =>
        Draw(r, key, number.Value, NumberSpec.Of(number), value => number.Value = value, tip);

    public static void Draw(Rect r, string key, float value, NumberSpec spec, Action<float> set, string? tip = null)
    {
        Event e = Event.current;
        var track = new Rect(r.x + Knob / 2f, Mathf.Round(r.center.y - Track / 2f), r.width - ValueWidth - Knob - 6f, Track);
        var hit = new Rect(r.x, r.y, track.width + Knob, r.height);

        if (GUI.enabled && e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition))
        {
            s_dragging = key;
            Focus.Take(key);
            SetAt(track, spec, set, e.mousePosition.x);
            e.Use();
        }
        if (s_dragging == key)
        {
            if (e.type == EventType.MouseDrag)
            {
                SetAt(track, spec, set, e.mousePosition.x);
                e.Use();
            }
            // Let go anywhere (the window sees the release while the press began on it).
            else if (e.type == EventType.MouseUp || (e.type == EventType.Repaint && !Input.GetMouseButton(0)))
            {
                s_dragging = null;
                if (e.type == EventType.MouseUp) e.Use();
            }
        }

        Focus.Item(key, r, out int adjust);
        if (adjust != 0) set(spec.Snap(value + adjust * spec.Step));
        Popups.Tip(r, tip, key);

        if (e.type != EventType.Repaint) return;
        GUIStyle style = Widgets.Style(() => Theme.FieldText, GUI.skin.label);
        Widgets.Paint(new Rect(r.xMax - ValueWidth, r.y, ValueWidth, r.height), spec.Format(value), style);
        if (!Widgets.Themed) return;
        float t = Mathf.Clamp01(Mathf.InverseLerp(spec.Min, spec.Max, value));
        bool active = s_dragging == key || (GUI.enabled && hit.Contains(e.mousePosition));
        Fill(track, Theme.Faint, 2);
        Fill(new Rect(track.x, track.y, Mathf.Max(Track, track.width * t), Track), GUI.enabled ? Theme.Accent : Theme.Dim, 2);
        var knob = new Rect(Mathf.Round(track.x + track.width * t - Knob / 2f), Mathf.Round(r.center.y - Knob / 2f), Knob, Knob);
        Fill(knob, active ? Theme.Text : Theme.Secondary, (int)(Knob / 2f));
    }

    private static void SetAt(Rect track, NumberSpec spec, Action<float> set, float x) =>
        set(spec.Snap(Mathf.Lerp(spec.Min, spec.Max, Mathf.InverseLerp(track.x, track.xMax, x))));

    private static void Fill(Rect r, Color color, int radius)
    {
        Color previous = GUI.color;
        GUI.color = new Color(color.r, color.g, color.b, color.a * previous.a);
        Theme.DrawSliced(r, Theme.BadgeTint, radius);
        GUI.color = previous;
    }
}
