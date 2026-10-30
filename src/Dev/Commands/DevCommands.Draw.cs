using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

internal static partial class DevCommands
{
    // draw minimap on also prints the mapping and, per game icon, its screen point against ours.
    private static CommandResult DrawCommand(CommandCall call)
    {
        bool on = call.Lower(2) == "on";
        if (call.Lower(2) is not ("on" or "off")) return CommandResult.Error("draw: usage draw test|minimap|arrows|bars|roles on|off");
        switch (call.Lower(1))
        {
            case "test":
                s_drawTest = on;
                break;
            case "minimap":
                s_drawMinimap = on;
                if (on) return new CommandResult(DevOutput.Reply("draw", MinimapLines())) with { Settle = SettleFrames };
                break;
            case "arrows":
                s_drawArrows = on;
                break;
            case "bars":
                s_drawBars = on;
                break;
            case "roles":
                s_drawRoles = on;
                if (on) return CommandResult.Ok($"draw roles on: theme {Themes.Resolve(LookSettings.Theme.Value).Name}, colour vision {LookSettings.ColourVision.ValueLabel}, roles version {ColourRoles.Version}") with { Settle = SettleFrames };
                break;
            default:
                return CommandResult.Error($"unknown command: {call.Line}");
        }
        if (on) World.WakeFor(600f);
        return CommandResult.Ok($"draw {call.Lower(1)} {(on ? "on" : "off")}") with { Settle = SettleFrames };
    }

    private static List<string> MinimapLines()
    {
        World.WakeFor(600f);
        if (!MinimapMap.Ready) return new List<string> { "ok draw minimap on: no minimap mapping now (no minimap, hidden, or visuals: minimap missing)" };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rect = MinimapMap.ScreenRect;
        var (sx, sy, rotation) = MinimapMap.Scale;
        var lines = new List<string>
        {
            $"ok draw minimap on: rect ({rect.x.ToString("F0", inv)}, {rect.y.ToString("F0", inv)}, {rect.width.ToString("F0", inv)} x {rect.height.ToString("F0", inv)}), "
                + $"scale {sx.ToString("F3", inv)} / {sy.ToString("F3", inv)} px/m, rotation {rotation.ToString("F1", inv)}, source {MinimapMap.Source}",
        };
        foreach (var (name, game, ours) in MinimapMap.Icons())
        {
            if (!rect.Contains(game)) continue;
            string d = ours is Vector2 o ? Vector2.Distance(o, game).ToString("F2", inv) + " px" : "-";
            lines.Add($"{name}: game ({game.x.ToString("F1", inv)}, {game.y.ToString("F1", inv)}) ours ({ours?.x.ToString("F1", inv)}, {ours?.y.ToString("F1", inv)}) off {d}");
        }
        return lines;
    }

    // Markers, arrows and bars for the dev checks, from the world model.
    private static void DrawWorldTests()
    {
        if (s_drawMinimap)
        {
            World.InRange(new WorldQuery { Kind = WorldKind.Enemy }, s_drawList);
            foreach (var e in s_drawList.OrderBy(e => e.Distance).Take(10)) Draw.MinimapMarker(e.Position, Theme.Pause);
            World.InRange(new WorldQuery { Kind = WorldKind.Shrine, Alive = false }, s_drawList);
            foreach (var e in s_drawList) Draw.MinimapMarker(e.Position, Theme.Good);
            World.InRange(new WorldQuery { Kind = WorldKind.Chest, Alive = false }, s_drawList);
            foreach (var e in s_drawList) Draw.MinimapMarker(e.Position, Theme.Accent);
        }
        if (s_drawArrows)
        {
            World.InRange(new WorldQuery { Kind = WorldKind.Enemy, MinRarity = Rarity.Rare }, s_drawList);
            foreach (var e in s_drawList.OrderBy(e => e.Distance).Take(10)) Draw.EdgeArrow(e.Position, Theme.Pause);
        }
        if (s_drawBars)
        {
            World.InRange(new WorldQuery { Kind = WorldKind.Enemy }, s_drawList);
            foreach (var e in s_drawList.OrderBy(e => e.Distance).Take(10))
            {
                if (e.HealthFraction is not float health) continue;
                float? ward = e.Ward is float w && e.MaxHealth is float max && max > 0f ? w / max : null;
                Draw.WorldBar(e.Position, 2.4f, health, ward, Draw.DefaultBars);
            }
            if (Player.Transform is Transform me && PlayerModel.Vitals is { } vitals)
            {
                float? ward = vitals.Ward is float w && vitals.MaxHealth > 0f ? w / vitals.MaxHealth : null;
                Draw.WorldBar(me.position, -0.3f, vitals.HealthFraction, ward, Draw.DefaultBars, 80f, 7f);
            }
        }
    }

    // The colour role swatch: a panel with each role's colour and name (the current theme and palette), and a marker in
    // each enemy rarity's colour around the player.
    private static void DrawRoles()
    {
        const float row = 18f, width = 190f;
        var panel = new Rect(16f, 120f, width, ColourRoles.Count * row + 12f);
        Draw.FillRect(panel, new Color(0f, 0f, 0f, 0.75f));
        for (int i = 0; i < ColourRoles.Count; i++)
        {
            var swatch = new Rect(panel.x + 6f, panel.y + 6f + i * row + 2f, 22f, row - 4f);
            Draw.FillRect(swatch, ColourRoles.Get(ColourRoles.Names[i]));
            GUI.Label(new Rect(swatch.xMax + 6f, swatch.y - 2f, width - 40f, row), ColourRoles.Names[i]);
        }
        if (Player.Transform is not Transform me) return;
        foreach (Rarity rarity in Enum.GetValues(typeof(Rarity)))
        {
            float angle = (int)rarity * Mathf.PI / 2f;
            Draw.WorldMarker(me.position + new Vector3(Mathf.Cos(angle) * 2.5f, 1f, Mathf.Sin(angle) * 2.5f), ColourRoles.Enemy(rarity), 16f);
        }
    }

    /// <summary>The draw test pattern around the player (a circle, a line, a label and a marker), and the other draw tests.</summary>
    public static void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        DrawWorldTests();
        if (Modules.Loaded("interact")) InteractCommands.DrawOverlay();
        if (s_drawRoles) DrawRoles();
        if (!s_drawTest || Player.Transform is not Transform t) return;
        Vector3 p = t.position;
        Draw.WorldCircle(p, 3f, Theme.Accent, 2f);
        Draw.WorldLine(p, p + new Vector3(3f, 0f, 0f), Theme.Pause, 2f);
        Draw.WorldMarker(p + new Vector3(0f, 2.5f, 0f), Theme.Good, 14f);
        Draw.WorldLabel(p + new Vector3(0f, 3.5f, 0f), "Draw test", Theme.Text);
    }
}
