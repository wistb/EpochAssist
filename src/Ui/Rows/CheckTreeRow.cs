using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

// A checkbox tree (the profile export and import): 20 px rows on tree lines with chevrons. Checking a parent checks
// its leaves; a parent shows mixed while only some are. Enter or Space checks the focused row, Left and Right close and
// open it. Top-level nodes start open, deeper ones closed.
internal static class CheckTreeRows
{
    public static void Register() => RowKinds.Register<CheckTree>(new Renderer());

    private const float RowHeight = 20f, Indent = 18f;

    // Opened nodes below the top level, and closed top-level ones, by tree and node key.
    private static readonly HashSet<string> s_toggled = new();

    private static bool IsOpen(CheckTree tree, CheckNode node, int depth) => s_toggled.Contains(tree.Key + "/" + node.Key) ^ (depth == 0);

    private static void Toggle(CheckTree tree, CheckNode node)
    {
        string key = tree.Key + "/" + node.Key;
        if (!s_toggled.Remove(key)) s_toggled.Add(key);
    }

    private static IEnumerable<string> Leaves(CheckNode node) => node.Children.Count == 0 ? new[] { node.Key } : node.Children.SelectMany(Leaves);

    private static int StateOf(CheckTree tree, CheckNode node)
    {
        int chosen = 0, total = 0;
        foreach (string leaf in Leaves(node))
        {
            total++;
            if (tree.Chosen.Contains(leaf)) chosen++;
        }
        return chosen == 0 ? 0 : chosen == total ? 1 : 2;
    }

    private static void Flip(CheckTree tree, CheckNode node)
    {
        var leaves = Leaves(node).ToList();
        if (StateOf(tree, node) == 1) foreach (string leaf in leaves) tree.Chosen.Remove(leaf);
        else foreach (string leaf in leaves) tree.Chosen.Add(leaf);
    }

    // The rows shown, in order, with their depth, whether each is its parent's last child, and the ancestors' "last" flags.
    private static List<(CheckNode Node, int Depth, bool Last, bool[] Ancestors)> Rows(CheckTree tree)
    {
        var rows = new List<(CheckNode, int, bool, bool[])>();
        void Add(IReadOnlyList<CheckNode> nodes, int depth, bool[] ancestors)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                bool last = i == nodes.Count - 1;
                rows.Add((nodes[i], depth, last, ancestors));
                if (nodes[i].Children.Count > 0 && IsOpen(tree, nodes[i], depth)) Add(nodes[i].Children, depth + 1, ancestors.Append(last).ToArray());
            }
        }
        Add(Safe.Call(tree.Nodes, Array.Empty<CheckNode>(), tree.Key), 0, Array.Empty<bool>());
        return rows;
    }

    private sealed class Renderer : RowRenderer<CheckTree>
    {
        protected override float Height(CheckTree row, float width, RowContext ctx) => Rows(row).Count * RowHeight + RowLayout.RowGap;

        protected override void Draw(CheckTree tree, Rect rect, RowContext ctx)
        {
            GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label), value = Widgets.Style(() => Theme.FieldDim, GUI.skin.label);
            float y = rect.y;
            foreach (var (node, depth, last, ancestors) in Rows(tree))
            {
                var r = new Rect(rect.x, y, rect.width, RowHeight);
                y += RowHeight;
                float x = r.x + depth * Indent;
                bool parent = node.Children.Count > 0;
                bool open = parent && IsOpen(tree, node, depth);
                var chevron = new Rect(x, r.y, 14f, r.height);
                var box = new Rect(x + 16f, r.y, 12f, r.height);
                var text = new Rect(box.xMax + 7f, r.y, r.xMax - box.xMax - 7f, r.height);
                bool chevronClicked = parent && GUI.Button(chevron, GUIContent.none, GUIStyle.none);
                bool clicked = GUI.Button(new Rect(box.x, r.y, r.xMax - box.x, r.height), GUIContent.none, GUIStyle.none);
                string key = $"{tree.Key}.{node.Key}";
                bool activated = Focus.Item(key, r, out int adjust);
                if (chevronClicked || (parent && adjust != 0 && (adjust > 0) != open)) Toggle(tree, node);
                if (clicked || activated) Flip(tree, node);
                if (Event.current.type != EventType.Repaint) continue;

                if (Widgets.Themed)
                {
                    Color line = new(Theme.Faint.r, Theme.Faint.g, Theme.Faint.b, 0.8f);
                    for (int i = 1; i < ancestors.Length; i++)
                    {
                        if (!ancestors[i]) Theme.Fill(new Rect(r.x + i * Indent - Indent + 6f, r.y, 1f, r.height), line);
                    }
                    if (depth > 0)
                    {
                        // From under the parent's chevron to just before this row's.
                        float lx = x - Indent + 6f;
                        Theme.Fill(new Rect(lx, r.y, 1f, last ? r.height / 2f : r.height), line);
                        Theme.Fill(new Rect(lx, r.center.y, x - lx - 1f, 1f), line);
                    }
                    if (parent) Theme.Tinted(new Rect(chevron.x + 1f, Mathf.Round(r.center.y - 6f), 12f, 12f), open ? Theme.Chevron : Theme.ChevronRight, Theme.Dim);
                }
                Controls.Check(box, StateOf(tree, node), GUI.enabled && r.Contains(Event.current.mousePosition));
                float valueWidth = node.Value != null ? Mathf.Min(TextCache.Width(node.Value, value), 120f) : 0f;
                Widgets.Paint(new Rect(text.x, text.y, text.width - valueWidth - 8f, text.height), node.Label, label);
                if (node.Value != null) Widgets.Paint(new Rect(r.xMax - valueWidth - value.padding.horizontal, r.y, valueWidth + value.padding.horizontal, r.height),
                    Controls.Fit(node.Value, value, 120f), value);
            }
        }

        protected override IEnumerable<string> Search(CheckTree row) => Safe.Call(row.Nodes, Array.Empty<CheckNode>(), row.Key).Select(n => n.Label);

        protected override RowText Bridge(CheckTree row, RowContext ctx) => new("checks", row.Key, row.Chosen.Count.ToString(), ctx.Enabled);
    }
}
