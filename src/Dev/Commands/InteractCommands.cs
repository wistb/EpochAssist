using System.Globalization;
using EpochAssist.Core;
using Il2Cpp;
using UnityEngine;

namespace EpochAssist.Dev;

// interact list, trace, draw and try: what the game offers to use near the player, the game's own click path, and one call of
// a kind's path from in range (offline). Read straight from the game, so they work before any feature uses them.
internal static class InteractCommands
{
    public const string Usage = "interact | interact list [n] | interact trace on|off | interact draw on|off | interact try <kind> nearest";
    private const string Kinds = "shrines, chests, items, gold, potions, tomes, favour, bones";
    private const int DefaultList = 10;
    // Kept below the game's reach, so a frame of movement can't put the call past the edge.
    private const float ReachMargin = 0.1f;
    private const float ConfirmSeconds = 1f;

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    public static CommandResult Run(CommandCall call) => call.Lower(1) switch
    {
        "" => new CommandResult(DevOutput.Reply("interact", new List<string> { "ok interact:" }.Concat(Features.Interact.Describe()).ToList())),
        "list" => List(call),
        "trace" => Trace(call),
        "try" => Try(call),
        "draw" => DrawSwitch(call),
        _ => CommandResult.Error($"interact: usage {Usage}"),
    };

    // One object near the player: its kind, name, distance, the game's reach for it, and what else the kind tells.
    private sealed class Row
    {
        public string Kind = string.Empty;
        public string Name = string.Empty;
        public float Distance;
        public float? Reach;
        public string Extra = string.Empty;
        // For try: the object to check and the call to make.
        public UnityEngine.Object? Target;
        public Func<bool>? Done;
        public Action? Use;
    }

    // ---- list ----

    private static CommandResult List(CommandCall call)
    {
        int count = DefaultList;
        if (call.Args.Length > 1 && (!int.TryParse(call.Lower(2), out count) || count < 1)) return CommandResult.Error($"interact: usage {Usage}");
        if (Player.Transform is not { } player || !Game.Alive(player)) return CommandResult.Error("interact list: no player");
        var rows = Collect(player.position, null);
        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var p = player.position;
        var lines = new List<string> { $"ok interact list: {Math.Min(count, rows.Count)} of {rows.Count}, player={F(p.x)},{F(p.y)},{F(p.z)}, online={GameState.Online?.ToString() ?? "?"}" };
        foreach (var row in rows.Take(count)) lines.Add(Line(row));
        return new CommandResult(DevOutput.Reply("interact", lines));
    }

    private static string Line(Row row) =>
        $"{row.Kind.PadRight(8)} {F(row.Distance).PadLeft(7)} m  reach {(row.Reach is { } r ? F(r) : "?").PadLeft(5)}  {row.Name}{(row.Extra.Length > 0 ? "  " + row.Extra : string.Empty)}";

    // Every kind, or only one, with its distance from the player.
    private static List<Row> Collect(Vector3 player, string? only)
    {
        var rows = new List<Row>();
        bool Wants(string kind) => only == null || only == kind;
        if (Wants("shrines") || Wants("chests"))
        {
            World.Want(null);
            World.WakeFor(5f);
            foreach (var entry in World.Entries)
            {
                if (entry.Gone) continue;
                string kind = entry.Kind switch { WorldKind.Shrine => "shrines", WorldKind.Chest => "chests", _ => string.Empty };
                if (kind.Length == 0 || !Wants(kind)) continue;
                rows.Add(ObjectRow(kind, entry, player));
            }
        }
        if (Wants("items")) AddItems(rows, player);
        if (Wants("gold")) AddNonItems(rows, "gold", GroundGoldVisuals.all, v => v.label, v => v.worldObjectClickListener, v => $"{v.goldValue} gold", player);
        if (Wants("potions")) AddNonItems(rows, "potions", GroundPotionVisuals.all, v => v.label, v => v.worldObjectClickListener, v => $"{v.potionType}", player);
        if (Wants("tomes")) AddNonItems(rows, "tomes", GroundXPTomeVisuals.all, v => v.label, v => v.worldObjectClickListener, v => $"{v.experience} xp", player);
        if (Wants("favour")) AddNonItems(rows, "favour", GroundFavorTomeVisuals.all, v => v.label, v => v.worldObjectClickListener, v => $"{v.favor} favour", player);
        if (Wants("bones")) AddNonItems(rows, "bones", GroundAncientBoneVisuals.all, v => v.label, v => v.worldObjectClickListener, v => $"{v.amount} bones", player);
        return rows;
    }

    private static Row ObjectRow(string kind, WorldEntry entry, Vector3 player)
    {
        var row = new Row { Kind = kind, Name = entry.Name ?? "?", Distance = Vector3.Distance(player, entry.Position) };
        var obj = entry.Object;
        var listener = Game.Alive(obj) ? obj!.GetComponent<WorldObjectClickListener>() ?? obj.GetComponentInChildren<WorldObjectClickListener>() : null;
        var handler = entry.Handler;
        string used = entry.Used is { } u ? u.ToString() : "?";
        if (!Game.Alive(listener))
        {
            row.Extra = $"used={used} listener=none object={(Game.Alive(obj) ? obj!.name : "none")}";
            return row;
        }
        row.Reach = listener!.interactionRange;
        var at = listener.transform.position;
        row.Distance = Vector3.Distance(player, at);
        row.Extra = $"used={used} requiresClick={listener.requiresClick} canInteract={listener.CanInteract()} flat={F(Flat(player, at))} object={listener.gameObject.name}"
            + (Game.Alive(handler) && Player.Object is { } p && Game.Alive(p) ? $" ready={handler!.CheckConditionReadiness(p)} triggered={handler.triggered}" : string.Empty);
        row.Target = listener;
        row.Done = () => entry.Used == true || (Game.Alive(handler) && handler!.triggered);
        // The feature's own use path, so the facts and the feature share one copy of it.
        var target = Features.Interact.ObjectTarget(kind == "shrines" ? Features.Interact.Shrines : Features.Interact.Chests, row.Name, listener, listener.interactionRange);
        row.Use = target.Use;
        return row;
    }

    private static void AddItems(List<Row> rows, Vector3 player)
    {
        var all = GroundItemVisuals.all;
        if (all == null) return;
        for (int i = 0; i < all.Count; i++)
        {
            var visual = all[i];
            if (!Game.Alive(visual)) continue;
            var label = visual.label;
            var item = visual.itemData;
            var at = visual.transform.position;
            var entry = item != null ? GameData.Item(item) : null;
            var row = new Row { Kind = "items", Name = entry?.Name ?? item?.FullName ?? "?", Distance = Vector3.Distance(player, at), Target = visual };
            string type = item != null ? $"type={item.itemType}/{item.subType} base={entry?.BaseName ?? "?"}" : "type=?";
            if (Game.Alive(label))
            {
                row.Reach = GroundItemLabel.pickupDistance;
                row.Extra = $"{type} flat={F(Flat(player, at))} distantList={label.inDistantPickupList} filter={label.lastFilterResult} rule={label.CurrentRuleNumber} "
                    + $"listed={label.isAddedToAll} emphasized={label.emphasized} shown={label.gameObject.activeInHierarchy}";
                row.Use = Features.Interact.ItemTarget(Features.Interact.Materials, row.Name, visual, visual.id, GroundItemLabel.pickupDistance).Use;
            }
            else
            {
                row.Extra = $"{type} label=none";
            }
            row.Done = () => !Game.Alive(visual) || !visual.gameObject.activeInHierarchy;
            rows.Add(row);
        }
    }

    // Gold, potions, tomes and bones: the label's own click; the reach is the click listener's.
    private static void AddNonItems<TVisual, TLabel>(List<Row> rows, string kind, DList<TVisual>? all, Func<TVisual, TLabel?> label,
        Func<TVisual, WorldObjectClickListener?> listenerOf, Func<TVisual, string> what, Vector3 player)
        where TVisual : MonoBehaviour where TLabel : NonItemPickupableGroundLabel<TVisual>
    {
        if (all == null) return;
        for (int i = 0; i < all.Count; i++)
        {
            var visual = all[i];
            if (!Game.Alive(visual)) continue;
            var at = visual.transform.position;
            var row = new Row { Kind = kind, Name = what(visual), Distance = Vector3.Distance(player, at), Target = visual };
            var listener = listenerOf(visual);
            if (Game.Alive(listener)) row.Reach = listener!.interactionRange;
            var l = label(visual);
            row.Extra = $"flat={F(Flat(player, at))} label={(Game.Alive(l) ? "yes" : "none")} requiresClick={(Game.Alive(listener) ? listener!.requiresClick.ToString() : "?")}";
            if (Game.Alive(l)) row.Use = l!.Clicked;
            row.Done = () => !Game.Alive(visual) || !visual.gameObject.activeInHierarchy;
            rows.Add(row);
        }
    }

    // ---- trace ----

    private static CommandResult Trace(CommandCall call)
    {
        switch (call.Lower(2))
        {
            case "on": InteractTraceFeature.On = true; break;
            case "off": InteractTraceFeature.On = false; break;
            case "": break;
            default: return CommandResult.Error($"interact: usage {Usage}");
        }
        bool running = FeatureHost.All.Any(f => f.Id == "interacttrace" && f.State == FeatureState.Running);
        return CommandResult.Ok($"interact trace: {(InteractTraceFeature.On ? "on" : "off")}{(running ? string.Empty : " (trace feature not running)")}");
    }

    // ---- try ----

    private static CommandResult Try(CommandCall call)
    {
        string kind = call.Lower(2);
        if (call.Lower(3) != "nearest" || !Kinds.Split(", ").Contains(kind)) return CommandResult.Error($"interact try: usage interact try <{Kinds.Replace(", ", "|")}> nearest");
        if (GameState.Online != false) return CommandResult.Error("interact try: offline only (use the feature online)");
        if (Player.Transform is not { } player || !Game.Alive(player) || !Game.Alive(Player.Object)) return CommandResult.Error("interact try: no player");
        var start = player.position;
        var rows = Collect(start, kind);
        // Shrines and chests already used are left out: a used one says nothing about the path.
        var row = rows.Where(r => r.Use != null && r.Done?.Invoke() != true).OrderBy(r => r.Distance).FirstOrDefault();
        if (row == null) return CommandResult.Error($"interact try {kind}: none to use");
        if (row.Reach is not { } reach) return CommandResult.Error($"interact try {kind}: {row.Name} at {F(row.Distance)} m has no reach to check");
        if (row.Distance > reach - ReachMargin) return CommandResult.Error($"interact try {kind}: {row.Name} at {F(row.Distance)} m is beyond its reach {F(reach)} - {F(ReachMargin)} m");
        string before = $"player={F(start.x)},{F(start.z)} moving={Moving()}";
        try
        {
            row.Use!();
        }
        catch (Exception e)
        {
            return CommandResult.Error($"interact try {kind}: {row.Name}: call failed: {e.Message}");
        }
        float until = Time.unscaledTime + ConfirmSeconds;
        string line = $"interact try {kind}: {row.Name} at {F(row.Distance)} m (reach {F(reach)}), before {before}";
        return new CommandResult(string.Empty)
        {
            Pending = () =>
            {
                if (Time.unscaledTime < until) return null;
                var after = Game.Alive(Player.Transform) ? Player.Transform!.position : start;
                return $"ok {line}; after 1 s: done={row.Done?.Invoke().ToString() ?? "?"} alive={Game.Alive(row.Target)} moved={F(Vector3.Distance(start, after))} m moving={Moving()} {row.Extra}";
            },
        };
    }

    private static string Moving() => PlayerModel.Moving?.ToString() ?? "?";

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

    private static string F(float value) => value.ToString("F2", s_inv);

    // ---- draw ----

    private const float DrawRange = 60f;
    // Shrines and chests are drawn from further away, with an arrow at the screen's edge, so they can be found.
    private const float KnownRange = 300f;
    private const float DrawRefreshSeconds = 0.5f;
    private static bool s_draw;
    private static float s_drawNextAt;

    // A click listener near the player: where it is, its name and range, and whether it is a shrine or chest the world
    // model knows (a display name) or something else the game lets the player click.
    private readonly record struct Mark(Transform At, string Text, float Range, bool Known, bool Used);
    private static readonly List<Mark> s_marks = new();

    private static CommandResult DrawSwitch(CommandCall call)
    {
        if (call.Lower(2) is not ("on" or "off")) return CommandResult.Error($"interact: usage {Usage}");
        SetDraw(call.Lower(2) == "on");
        return CommandResult.Ok($"interact draw {(s_draw ? "on" : "off")}");
    }

    /// <summary>Whether the object names overlay is on.</summary>
    public static bool Drawing => s_draw;

    public static void SetDraw(bool on)
    {
        s_draw = on;
        s_marks.Clear();
        s_drawNextAt = 0f;
        if (on) World.WakeFor(600f);
    }

    // Labels every active click listener within 60 m (shrines and chests within 300 m, with an edge arrow): name, range,
    // distance, and a ring at its range. The list is read from the game's own listener list twice a second, not every
    // frame.
    public static void DrawOverlay()
    {
        if (!s_draw || Player.Transform is not { } player || !Game.Alive(player)) return;
        var p = player.position;
        if (Time.unscaledTime >= s_drawNextAt)
        {
            s_drawNextAt = Time.unscaledTime + DrawRefreshSeconds;
            RefreshMarks(p);
        }
        foreach (var mark in s_marks)
        {
            if (!Game.Alive(mark.At)) continue;
            var at = mark.At.position;
            var colour = mark.Known ? Ui.Theme.Good : Ui.Theme.Pause;
            // A used one keeps its label but loses its arrow, so the arrows lead only to what is left to use.
            if (mark.Known && !mark.Used) Draw.EdgeArrow(at, colour);
            Draw.WorldCircle(at, mark.Range, colour, 2f);
            Draw.WorldMarker(at + new Vector3(0f, 1.5f, 0f), colour, 10f);
            Draw.WorldLabel(at + new Vector3(0f, 2.5f, 0f), $"{mark.Text} ({F(Vector3.Distance(p, at))} m, reach {F(mark.Range)})", Ui.Theme.Text);
        }
    }

    private static void RefreshMarks(Vector3 player)
    {
        s_marks.Clear();
        var names = new Dictionary<IntPtr, (string Name, bool Used)>();
        foreach (var entry in World.Entries)
        {
            if (entry.Gone || entry.Kind is not (WorldKind.Shrine or WorldKind.Chest) || !Game.Alive(entry.Object)) continue;
            var listener = entry.Object!.GetComponent<WorldObjectClickListener>() ?? entry.Object.GetComponentInChildren<WorldObjectClickListener>();
            if (Game.Alive(listener)) names[listener!.Pointer] = ($"{entry.Kind}: {entry.Name ?? "?"}{(entry.Used == true ? " (used)" : string.Empty)}", entry.Used == true);
        }
        var all = WorldObjectClickListener.allListener;
        if (all == null) return;
        for (int i = 0; i < all.Count; i++)
        {
            var listener = all[i];
            if (!Game.Alive(listener) || !listener.isActiveAndEnabled) continue;
            var t = listener.transform;
            bool known = names.TryGetValue(listener.Pointer, out var named);
            if (Vector3.Distance(player, t.position) > (known ? KnownRange : DrawRange)) continue;
            s_marks.Add(new Mark(t, known ? named.Name : SpawnedName(t), listener.interactionRange, known, known && named.Used));
        }
    }

    // The listener's own name is often a bare "Trigger" or "Clicker": lead with the spawned object it belongs to.
    private static string SpawnedName(Transform t)
    {
        string leaf = t.gameObject.name;
        for (var parent = t; Game.Alive(parent); parent = parent!.parent)
        {
            string name = parent!.gameObject.name;
            if (!name.Contains("(Clone)")) continue;
            name = name.Replace("(Clone)", string.Empty).Trim();
            return parent == t ? name : $"{name} > {leaf}";
        }
        return leaf;
    }
}
