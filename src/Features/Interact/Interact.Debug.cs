using EpochAssist.Core;
using UnityEngine;
using S = EpochAssist.Features.InteractSettings;

namespace EpochAssist.Features;

// Show reach: a ring around the hero for each enabled kind's reach (items share one; objects use the largest game reach
// seen this zone, under the player's Reach), and a marker on what the feature uses next. Nothing is read while off.
internal static partial class Interact
{
    private const float RingAlpha = 0.85f;
    private static readonly Color s_next = new(1f, 1f, 1f, 0.95f);

    /// <summary>The rings and the next-use marker, from the feature's OnGUI (repaint passes only).</summary>
    public static void OnGUI()
    {
        if (!S.ShowReach.Value || s_run is not { } run || Event.current?.type != EventType.Repaint) return;
        if (Player.Transform is not { } player || !Game.Alive(player)) return;
        var at = player.position;
        if (KindOn(Keys) || KindOn(Materials) || KindOn(Filtered)) Draw.WorldCircle(at, Math.Min(S.Reach.Value, ItemReach) - ReachMargin, RingColor(S.ItemRingColor), 2f);
        float objects = 0f;
        foreach (var kind in Kinds)
        {
            if (!kind.IsItem && KindOn(kind) && run.GameReach.TryGetValue(kind.Id, out var reach)) objects = Math.Max(objects, reach.Max);
        }
        if (objects > 0f) Draw.WorldCircle(at, Math.Min(S.Reach.Value, objects) - ReachMargin, RingColor(S.ObjectRingColor), 2f);
        if (NextPoint(run) is { } next) Draw.WorldMarker(next + Vector3.up * 1.5f, s_next, 14f);
    }

    // The player's colour, else the setting's default (the one its Default swatch shows), a little see-through.
    private static Color RingColor(ColorSetting setting)
    {
        if (!setting.TryGetColor(out var color)) ColorSetting.TryParse(setting.EmptyHex, out color);
        return new Color(color.r, color.g, color.b, RingAlpha);
    }

    // What the feature is using now, else the best candidate of its last scan.
    private static Vector3? NextPoint(Run run)
    {
        var c = run.Phase != Phase.Idle ? run.Current : run.HasBest ? run.Best : default;
        if (c.Listener is { } listener && Game.Alive(listener)) return listener.transform.position;
        if (c.Visual is { } visual && Game.Alive(visual)) return visual.transform.position;
        return null;
    }
}
