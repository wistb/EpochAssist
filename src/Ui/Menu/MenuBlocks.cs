using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// Hand-drawn parts of the menu (profiles, diagnostics, the console), by name. A page or a row places one; it gets a
/// rect and may lay itself out with GUILayout inside it. Each also gives the bridge its lines, and, when it sits among
/// rows, its height.
/// </summary>
internal static class MenuBlocks
{
    private sealed record Block(Action<Rect, object?> Draw, Func<object?, IEnumerable<string>> Bridge, Func<object?, float, float>? Height);

    private static readonly Dictionary<string, Block> s_blocks = new();

    public static void Register(string name, Action<Rect, object?> draw, Func<object?, IEnumerable<string>> bridge, Func<object?, float, float>? height = null) =>
        s_blocks[name] = new Block(draw, bridge, height);

    public static bool Has(string name) => s_blocks.ContainsKey(name);

    public static void Clear() => s_blocks.Clear();

    public static void Draw(string name, Rect rect, object? data)
    {
        if (!s_blocks.TryGetValue(name, out var block)) return;
        try
        {
            block.Draw(rect, data);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("menu:block:" + name, $"Menu block {name} failed: {e.Message}");
        }
    }

    public static float Height(string name, object? data, float width) =>
        s_blocks.TryGetValue(name, out var block) && block.Height != null ? Safe.Call(() => block.Height(data, width), 0f, "block." + name) : 0f;

    public static IEnumerable<string> Bridge(string name, object? data) =>
        s_blocks.TryGetValue(name, out var block) ? Safe.Call(() => block.Bridge(data).ToList(), new List<string>(), "block." + name) : Enumerable.Empty<string>();
}
