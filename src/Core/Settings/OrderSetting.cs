using System.Text.Json.Nodes;

namespace EpochAssist.Core;

/// <summary>
/// An order of ids (the mini HUD's sections), stored as one comma-separated text; empty keeps the natural order. Ids the
/// stored order doesn't name go after the named ones, in their natural order, so a new feature finds its place. Shown
/// by its page as a list to drag, not as a setting row.
/// </summary>
internal sealed class OrderSetting : Setting<string>
{
    public OrderSetting(Section section, string id, string label) : base(section, id, label, string.Empty) { }

    /// <summary>The ids in effect, as stored.</summary>
    public IReadOnlyList<string> Ids => Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The given items, the stored order's first, then the rest as given.</summary>
    public List<T> Sort<T>(IEnumerable<T> items, Func<T, string> idOf)
    {
        var ids = Ids;
        return items.Select((item, i) => (Item: item, Rank: IndexOf(ids, idOf(item)) is int at and >= 0 ? at : ids.Count + i))
            .OrderBy(x => x.Rank).Select(x => x.Item).ToList();
    }

    /// <summary>Moves an id from one place to another in the given order and stores the whole order.</summary>
    public void Move(IReadOnlyList<string> current, int from, int to)
    {
        if (from < 0 || from >= current.Count || to < 0 || to >= current.Count || from == to) return;
        var ids = current.ToList();
        string moved = ids[from];
        ids.RemoveAt(from);
        ids.Insert(to, moved);
        Value = string.Join(",", ids);
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (int i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    internal override void Clamp() { }

    protected override bool TryRead(JsonValue json, out string value) => json.TryGetValue(out value!);
}
