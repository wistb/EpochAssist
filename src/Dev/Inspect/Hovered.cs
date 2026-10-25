using System.Reflection;
using EpochAssist.Core;
using EpochAssist.Ui;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using UnityEngine.EventSystems;

namespace EpochAssist.Dev;

// hovered: the game UI under the cursor through a UI raycast (no pointer events are sent), and the item behind an
// open tooltip. The last result is kept as the inspect targets hovered and hovered:item.
internal static class Hovered
{
    private const int MaxHits = 5;

    // Looked up by name at run time: a game update that renames it costs the item line, not the build.
    // Confirmed in game (1.5.3): the item tooltip on screen is this one, its item at cachedItem and tooltipActive true
    // while shown; Il2CppLE.UI.Tooltips.ItemTooltip exists but stays inactive.
    private const string ItemTooltipType = "Il2Cpp.UITooltipItem";

    private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.Instance;

    public static bool HasRun { get; private set; }
    public static Il2CppObjectBase? LastHit { get; private set; }
    public static Il2CppObjectBase? LastItem { get; private set; }

    public static void Reset()
    {
        HasRun = false;
        LastHit = null;
        LastItem = null;
    }

    public static string Run()
    {
        Reset();
        HasRun = true;
        Vector3 mouse = Input.mousePosition;
        // Our UI works in screen pixels from the top left; Unity's mouse position is from the bottom left.
        var topLeft = new Vector2(mouse.x, Screen.height - mouse.y);
        string at = $"at ({mouse.x:F0}, {mouse.y:F0})";
        string focus = $", focused {Application.isFocused}";
        if (Menu.Visible && Menu.ScreenRect.Contains(topLeft)) return $"ok hovered {at}{focus}: our menu";
        if (InputGate.HudHitTest?.Invoke(topLeft) == true) return $"ok hovered {at}{focus}: our HUD";

        var events = EventSystem.current;
        if (events == null) return $"ok hovered {at}{focus}: no event system";
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        events.RaycastAll(new PointerEventData(events) { position = new Vector2(mouse.x, mouse.y) }, results);
        // The camera's physics raycaster answers too; UI hits are the ones on a RectTransform, the rest are the world.
        var all = Enumerable.Range(0, results.Count).Select(i => results[i].gameObject).Where(g => g != null).ToList();
        var hits = all.Where(g => g.transform.TryCast<RectTransform>() != null).Take(MaxHits).ToList();
        var world = all.FirstOrDefault(g => g.transform.TryCast<RectTransform>() == null);

        var lines = new List<string>();
        if (hits.Count == 0)
        {
            lines.Add($"ok hovered {at}{focus}: no game UI");
        }
        else
        {
            LastHit = hits[0];
            lines.Add($"ok hovered {at}{focus}: {Describe(hits[0])}");
            lines.AddRange(hits.Skip(1).Select(h => "  under: " + Describe(h)));
        }
        if (world != null) lines.Add("  world: " + Describe(world));
        lines.Add("  item: " + FindItem(hits.Count > 0 ? hits[0] : null));
        return DevOutput.Reply("hovered", lines);
    }

    private static string Describe(GameObject go) =>
        $"{go.name} [{string.Join(", ", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetIl2CppType().Name))}] {GameTypes.PathOf(go)}";

    // The first item data on the hit's parents' components or on an open item tooltip: a member of its own first
    // (ItemData, ItemDataUnpacked), then one member deep through the game's own types (ItemTooltip._info.Item,
    // GroundItemLabel.visuals.itemData).
    private static string FindItem(GameObject? hit)
    {
        var candidates = new List<object>();
        if (hit != null) candidates.AddRange(hit.GetComponentsInParent<Component>(true).Where(c => c != null).Select(GameTypes.Specific));
        var tooltip = GameTypes.Resolve(ItemTooltipType);
        if (tooltip.Type == null) return $"tooltip type {ItemTooltipType} not found";
        var (open, _) = GameTypes.Instances(tooltip);
        candidates.AddRange((open ?? new()).Where(i => i.Active).Select(i => i.Object.Value).Where(o => o != null && Shown(o))!);

        foreach (bool deeper in new[] { false, true })
        {
            foreach (var candidate in candidates)
            {
                if (ItemIn(candidate, deeper) is not { } item) continue;
                LastItem = item;
                return $"{Inspect.TypeName(item.GetType())} '{DisplayName(item)}' (on {Inspect.TypeName(candidate.GetType())})";
            }
        }
        return "none";
    }

    private static Il2CppObjectBase? ItemIn(object obj, bool deeper)
    {
        foreach (var p in obj.GetType().GetProperties(InstanceMembers))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            var type = p.PropertyType;
            // The data itself, not the ItemDataProvider interface, whose only member is a method.
            bool item = type.Name.Contains("ItemData") && !type.Name.EndsWith("Provider");
            bool game = type.Namespace?.StartsWith("Il2Cpp") == true && !type.Namespace.StartsWith("Il2CppSystem") && !type.IsValueType;
            if (!item && !(deeper && game)) continue;
            if (Value(p, obj) is not Il2CppObjectBase found || !Game.Alive(found)) continue;
            if (item) return found;
            foreach (var q in found.GetType().GetProperties(InstanceMembers))
            {
                if (q.GetIndexParameters().Length == 0 && q.PropertyType.Name.Contains("ItemData") && !q.PropertyType.Name.EndsWith("Provider")
                    && Value(q, found) is Il2CppObjectBase data && Game.Alive(data)) return data;
            }
        }
        return null;
    }

    // The tooltip object stays active between tooltips; its tooltipActive says whether one is shown.
    private static bool Shown(object tooltip) =>
        tooltip.GetType().GetProperty("tooltipActive", InstanceMembers) is not { } shown || Value(shown, tooltip) is not false;

    private static object? Value(PropertyInfo p, object obj)
    {
        try
        {
            return p.GetValue(obj);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DisplayName(object item)
    {
        foreach (string name in new[] { "FullName", "displayName", "name" })
        {
            try
            {
                if (item.GetType().GetProperty(name, InstanceMembers)?.GetValue(item) is string { Length: > 0 } s) return s;
            }
            catch (Exception)
            {
                // The next name, then none.
            }
        }
        return "?";
    }
}
