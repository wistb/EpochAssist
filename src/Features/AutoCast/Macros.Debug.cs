using EpochAssist.Core;
using UnityEngine;
using S = EpochAssist.Features.AutoCastSettings;

namespace EpochAssist.Features;

// The debug overlays (FR-063, R18), drawn with Draw in repaint passes only and off by default: "Debug conditions" lists
// each enabled macro of the class with its state and first failing line; "Debug ranges" rings each distinct range an
// enabled enemy line or target counts, around the hero or the cursor point, green while a line using it counts or the
// target has a pick, and marks each target's pick (the enemy, or the area spot with its circle); and up to four
// lines with "Show chart on overlay" get a 10-second chart of their value, sampled 10 times a second.
internal static partial class Macros
{
    private const float PanelX = 16f, PanelY = 170f, LineHeight = 18f, ChartWidth = 120f, ChartHeight = 40f, SampleSeconds = 0.1f;
    private const int MaxCharts = 4, ChartSamples = 100;
    private static readonly Color s_pass = new(0.35f, 1f, 0.45f, 0.85f);
    private static readonly Color s_fail = new(1f, 1f, 1f, 0.5f);
    private static readonly Color s_panel = new(0f, 0f, 0f, 0.55f);
    private static readonly Color s_target = new(1f, 0.75f, 0.3f, 0.9f);
    private static readonly List<(Vector3 At, string Text)> s_labels = new();

    // One charted line: whose it is and its last 10 s of values (NaN where it had none).
    private sealed class Chart
    {
        public Chart(string label) => Label = label;
        public string Label;
        public readonly float[] Values = Enumerable.Repeat(float.NaN, ChartSamples).ToArray();
        public int Next;
        public bool Seen;
    }

    private static readonly Dictionary<(string Macro, int Node), Chart> s_charts = new();
    private static float s_lastSample = float.NegativeInfinity;

    // From the engine's frame: samples the charted lines' last values 10 times a second.
    private static void SampleCharts(Engine engine, float now)
    {
        if (now - s_lastSample < SampleSeconds) return;
        s_lastSample = now;
        foreach (var chart in s_charts.Values) chart.Seen = false;
        int count = 0;
        foreach (var set in SetsOf(engine))
        {
            if (!set.On || !set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (!macro.On) continue;
                var nodes = macro.All.Nodes;
                for (int i = 0; i < nodes.Length && count < MaxCharts; i++)
                {
                    if (!nodes[i].Chart || nodes[i].IsGroup) continue;
                    count++;
                    if (!s_charts.TryGetValue((macro.Id, i), out var chart)) s_charts[(macro.Id, i)] = chart = new Chart($"{macro.Name}: {nodes[i].Words}");
                    chart.Seen = true;
                    float value = float.NaN;
                    if (engine.Checks.TryGetValue(macro, out var check) && ReferenceEquals(check.Tree.List, macro.All) && i < check.Tree.Results.Length)
                        value = check.Tree.Results[i].Number;
                    chart.Values[chart.Next] = value;
                    chart.Next = (chart.Next + 1) % ChartSamples;
                }
            }
        }
        // Lines no longer charted drop their chart.
        foreach (var key in s_charts.Where(c => !c.Value.Seen).Select(c => c.Key).ToList()) s_charts.Remove(key);
    }

    /// <summary>The overlays, from the feature's OnGUI (repaint passes only).</summary>
    public static void OnGUI()
    {
        if (s_engine is not { } engine || Event.current?.type != EventType.Repaint) return;
        if (S.DebugRanges.Value) DrawRanges(engine);
        if (S.DebugConditions.Value) DrawPanel(engine);
        DrawCharts();
    }

    private static void DrawPanel(Engine engine)
    {
        var lines = new List<(string Text, Color Color)>();
        foreach (var set in SetsOf(engine))
        {
            if (!set.On || !set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (!macro.On) continue;
                var (text, shape) = MacroState(set, macro);
                lines.Add(($"{macro.Name}: {text}", shape == StatusState.Active ? s_pass : Color.white));
            }
        }
        if (lines.Count == 0) lines.Add(($"No enabled macros for {engine.Class ?? "this class"}", Color.white));
        // Below the notifications when they would cover it (they stack down from the mini HUD).
        float y = PanelY;
        var panel = new Rect(PanelX - 6f, y - 4f, 460f, lines.Count * LineHeight + 8f);
        if (Toasts.ShownArea is { width: > 0f } toasts && toasts.Overlaps(panel))
        {
            y = toasts.yMax + 12f;
            panel.y = y - 4f;
        }
        Draw.FillRect(panel, s_panel);
        for (int i = 0; i < lines.Count; i++) Draw.ScreenLabel(new Vector2(PanelX, y + i * LineHeight), lines[i].Text, lines[i].Color);
    }

    // A ring per distinct range and centre the enemy lines in use count (the slots' lists and the class's enabled
    // macros), green while one using it counts.
    private static void DrawRanges(Engine engine)
    {
        var rings = new Dictionary<(float Range, bool Cursor), bool>();
        AutoCast.SlotRanges(rings);
        foreach (var set in SetsOf(engine))
        {
            if (!set.On || !set.AppliesTo(engine.Class)) continue;
            foreach (var macro in set.Macros)
            {
                if (!macro.On) continue;
                var check = engine.Checks.TryGetValue(macro, out var c) && ReferenceEquals(c.Tree.List, macro.All) ? c.Tree : null;
                var nodes = macro.All.Nodes;
                for (int i = 0; i < nodes.Length; i++)
                {
                    if (!nodes[i].On || nodes[i].IsGroup || nodes[i].Invalid != null || nodes[i].KindDef?.KeyFor(nodes[i].Values) is not { } key) continue;
                    bool counts = check != null && i < check.Counted.Length && check.Counted[i];
                    var ring = (key.Range, key.Cursor);
                    rings[ring] = (rings.TryGetValue(ring, out bool any) && any) || counts;
                }
            }
        }
        if (Player.Transform is not Transform hero) return;
        // Each target in use: its range joins the rings (green while it has a pick); the picked enemy gets a marker
        // and the target's words at its feet, an area spot its circle.
        // Targets on the same enemy or spot share one label (their words joined), so they don't print over each other.
        s_labels.Clear();
        for (int i = 0; i < Targets.Count; i++)
        {
            var pick = Targets.PickAt(i);
            var ring = (pick.Key.Range, false);
            rings[ring] = (rings.TryGetValue(ring, out bool any) && any) || pick.Has;
            Vector3? at = pick.Entry is { Gone: false } e ? e.Position : pick.Spot;
            if (at is not Vector3 point) continue;
            if (pick.Spot != null) Draw.WorldCircle(point, pick.Key.Radius, s_target, 2f);
            Draw.WorldMarker(point, s_target, 10f);
            string words = TargetSpec.FromKey(pick.Key).Words(TargetOwner.Condition);
            int near = s_labels.FindIndex(l => (l.At - point).sqrMagnitude < 6.25f);
            if (near >= 0) s_labels[near] = (s_labels[near].At, s_labels[near].Text + "\n" + words);
            else s_labels.Add((point, words));
        }
        foreach (var (at, text) in s_labels) Draw.WorldLabel(at, text, s_target);
        Vector3? cursor = FrameCursor();
        foreach (var ((range, atCursor), counts) in rings)
        {
            if (atCursor && cursor == null) continue;
            Draw.WorldCircle(atCursor ? cursor!.Value : hero.position, range, counts ? s_pass : s_fail, 2f);
        }
    }

    private static void DrawCharts()
    {
        int index = 0;
        foreach (var chart in s_charts.Values)
        {
            float x = PanelX, y = Screen.height - 80f - index * (ChartHeight + 26f);
            Draw.FillRect(new Rect(x - 4f, y - 18f, ChartWidth + 8f, ChartHeight + 22f), s_panel);
            Draw.ScreenLabel(new Vector2(x, y - 17f), chart.Label.Length > 34 ? chart.Label[..33] + "…" : chart.Label, Color.white);
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in chart.Values)
            {
                if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
            }
            if (min > max)
            {
                index++;
                continue;
            }
            if (max - min < 1e-3f) max = min + 1f;
            Vector2? previous = null;
            for (int n = 0; n < ChartSamples; n++)
            {
                float v = chart.Values[(chart.Next + n) % ChartSamples];
                if (float.IsNaN(v) || float.IsInfinity(v))
                {
                    previous = null;
                    continue;
                }
                var point = new Vector2(x + n * ChartWidth / (ChartSamples - 1), y + ChartHeight - (v - min) / (max - min) * ChartHeight);
                if (previous is Vector2 p) Draw.ScreenLine(p, point, s_pass, 1.5f);
                previous = point;
            }
            index++;
        }
    }
}
