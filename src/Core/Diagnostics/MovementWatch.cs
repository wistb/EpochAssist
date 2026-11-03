using System.Globalization;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// Notes the character's movement hiccups while the player holds movement (keys, or the left button for click-to-move):
/// a stop that resumes (STOP), a short swerve of the path (SWERVE) or of the facing (TURN) that comes back, a position
/// jump (JUMP) and a frozen frame (FREEZE). Each is kept with what the mod and the game's input were doing then, for the
/// diagnostics copy: a player's report then says whether the mod acted just before a hiccup. Runs every frame in every
/// build; it allocates only when it records one.
/// </summary>
internal static class MovementWatch
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;
    private const int Kept = 20;

    // Walking runs at several metres a second. Online the position can sit still for a frame or two between updates, so
    // a stop counts only once the character has stood still this long with movement held.
    private const float WalkingSpeed = 2f;
    private const float StillSpeed = 0.5f;
    private const float WalkSecondsBefore = 0.3f;
    private const float MinStopMs = 80f;
    private const float MaxStopSeconds = 3f;
    // More than this in a frame is the position being set (a snap back, a teleport skill, a portal).
    private const float JumpMetres = 1.5f;
    private const float FreezeMs = 250f;
    // A short change of course: this far off, back within this long.
    private const float VeerDegrees = 50f;
    private const float BackDegrees = 25f;
    private const float VeerSeconds = 0.5f;
    private const float HeadingWindow = 0.08f;
    private const float HeadingMinMetres = 0.15f;
    private const float BaseLag = 0.25f;

    /// <summary>Each recorded hiccup's line, as it is kept (the dev frame log writes them to a file too).</summary>
    public static event Action<string>? Recorded;

    /// <summary>Whether a skill is being cast now or was this long ago (unscaled seconds); set by the player model's
    /// owner so this stays apart from it. Null when it can't say.</summary>
    public static Func<float?>? SecondsSinceCast;
    /// <summary>The macro running now, if any; set by the Macros feature while it runs.</summary>
    public static Func<string?>? RunningMacro;

    private static readonly Queue<string> s_kept = new();
    private static int s_stops, s_swerves, s_turns, s_jumps, s_freezes;
    private static DateTime s_since = DateTime.Now;

    private static Vector3? s_lastPos;
    private static float s_walkTime;
    private static float s_lastGcAt = float.NegativeInfinity;
    private static int s_gc0 = -1;

    private sealed class Context
    {
        public float At;
        public string Before = string.Empty;
        public float WorstFrameMs;
        public bool Gate, Lock, MouseBlock, Menu, Hud, Unfocused, ModAction, Cast;
        public string? Action, Macro;
    }

    private sealed class Track
    {
        public readonly Queue<(float T, float Angle)> History = new();
        public float? VeerBase;
        public float VeerAt, VeerMax;
        public Context? Ctx;
    }

    private static Context? s_stop;
    private static readonly Queue<(float T, Vector3 P)> s_trail = new();
    private static readonly Track s_path = new(), s_facing = new();

    /// <summary>The counts since the start, for the summary line.</summary>
    public static string Summary =>
        $"stops {s_stops}, swerves {s_swerves}, turns {s_turns}, jumps {s_jumps}, freezes {s_freezes} since {s_since.ToString("HH:mm:ss", s_inv)}";

    // Frames watched in game, with movement held, and walking: for checking the watch itself.
    private static int s_watched, s_heldFrames, s_walkingFrames;

    /// <summary>What the watch has seen, for the dev frame log: frames in game, with movement held, walking.</summary>
    public static string Seen => $"watched {s_watched} frames, movement held in {s_heldFrames}, walking in {s_walkingFrames}";

    /// <summary>The kept hiccups, oldest first.</summary>
    public static List<string> Lines() => s_kept.ToList();

    public static void Reset()
    {
        s_kept.Clear();
        s_stops = s_swerves = s_turns = s_jumps = s_freezes = 0;
        s_watched = s_heldFrames = s_walkingFrames = 0;
        s_since = DateTime.Now;
        Forget();
    }

    /// <summary>Once a frame, from the diagnostics feature.</summary>
    public static void Frame()
    {
        int gc = GC.CollectionCount(0);
        if (gc != s_gc0)
        {
            if (s_gc0 >= 0) s_lastGcAt = Time.unscaledTime;
            s_gc0 = gc;
        }
        if (GameState.Loading || !GameState.InGame)
        {
            Forget();
            return;
        }
        var player = Player.Transform;
        if (player == null || !Game.Alive(player))
        {
            Forget();
            return;
        }
        float dt = Time.unscaledDeltaTime;
        var pos = player.position;
        bool held = MovementHeld();
        s_watched++;
        if (held) s_heldFrames++;
        if (dt * 1000f >= FreezeMs && held)
        {
            s_freezes++;
            Record($"FREEZE {(dt * 1000f).ToString("F0", s_inv)} ms frame", Open());
        }
        if (s_lastPos is { } last)
        {
            float moved = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(last.x, last.z));
            if (moved >= JumpMetres)
            {
                // A jump starts the course over: the path before it says nothing about the path after.
                if (held)
                {
                    s_jumps++;
                    Record($"JUMP {moved.ToString("F1", s_inv)} m", Open());
                }
                Forget();
                s_lastPos = pos;
                return;
            }
            if (dt > 0f)
            {
                if (moved / dt >= WalkingSpeed) s_walkingFrames++;
                WatchStop(moved / dt, dt, held);
                WatchVeer(pos, player.eulerAngles.y, held);
            }
        }
        s_lastPos = pos;
    }

    private static void Forget()
    {
        s_lastPos = null;
        s_walkTime = 0f;
        s_stop = null;
        ForgetCourse();
    }

    private static void ForgetCourse()
    {
        s_trail.Clear();
        Clear(s_path);
        Clear(s_facing);
    }

    private static void Clear(Track track)
    {
        track.History.Clear();
        track.VeerBase = null;
        track.Ctx = null;
    }

    // ---- Stops ----

    private static void WatchStop(float speed, float dt, bool held)
    {
        if (s_stop is { } stop)
        {
            float stoppedFor = Time.unscaledTime - stop.At;
            // Movement let go, or a long halt: the player stopped on purpose (or is fighting); not a hiccup.
            if (!held || stoppedFor > MaxStopSeconds)
            {
                s_stop = null;
                s_walkTime = 0f;
                return;
            }
            stop.WorstFrameMs = Math.Max(stop.WorstFrameMs, dt * 1000f);
            Note(stop);
            if (speed < WalkingSpeed) return;
            s_stop = null;
            s_walkTime = 0f;
            float ms = stoppedFor * 1000f;
            if (ms < MinStopMs) return;
            s_stops++;
            Record($"STOP {ms.ToString("F0", s_inv)} ms", stop);
            return;
        }
        if (speed >= WalkingSpeed)
        {
            s_walkTime += dt;
            return;
        }
        if (speed <= StillSpeed && held && s_walkTime >= WalkSecondsBefore)
        {
            s_stop = Open();
            s_stop.At -= dt;
            s_stop.WorstFrameMs = dt * 1000f;
            return;
        }
        if (speed <= StillSpeed) s_walkTime = 0f;
    }

    // ---- Swerves and turns ----

    private static void WatchVeer(Vector3 pos, float yaw, bool held)
    {
        if (!held)
        {
            if (s_trail.Count > 0) ForgetCourse();
            return;
        }
        float now = Time.unscaledTime;
        s_trail.Enqueue((now, pos));
        while (s_trail.Count > 1 && now - s_trail.Peek().T > HeadingWindow) s_trail.Dequeue();
        var from = s_trail.Peek().P;
        float dx = pos.x - from.x, dz = pos.z - from.z;
        if (dx * dx + dz * dz >= HeadingMinMetres * HeadingMinMetres) Follow(s_path, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, now, "SWERVE", ref s_swerves);
        // Facing counts only while walking, so turning on the spot to cast isn't taken for a turn.
        if (s_walkTime >= WalkSecondsBefore || s_facing.VeerBase != null) Follow(s_facing, yaw, now, "TURN", ref s_turns);
    }

    private static void Follow(Track track, float angle, float now, string kind, ref int count)
    {
        track.History.Enqueue((now, angle));
        while (track.History.Count > 1 && now - track.History.Peek().T > VeerSeconds + BaseLag) track.History.Dequeue();
        if (track.VeerBase is float veerBase)
        {
            float off = Mathf.Abs(Mathf.DeltaAngle(veerBase, angle));
            track.VeerMax = Math.Max(track.VeerMax, off);
            Note(track.Ctx!);
            if (off <= BackDegrees)
            {
                count++;
                Record($"{kind} {((now - track.VeerAt) * 1000f).ToString("F0", s_inv)} ms, off by up to {track.VeerMax.ToString("F0", s_inv)}°", track.Ctx!);
                track.VeerBase = null;
                track.Ctx = null;
            }
            else if (now - track.VeerAt > VeerSeconds)
            {
                // It stayed on the new course: the player turned.
                track.VeerBase = null;
                track.Ctx = null;
            }
            return;
        }
        // The course a little while ago; a change needs that much history to be measured.
        (float T, float Angle) baseline = track.History.Peek();
        foreach (var h in track.History)
        {
            if (now - h.T < BaseLag) break;
            baseline = h;
        }
        if (now - baseline.T < BaseLag * 0.8f) return;
        float start = Mathf.Abs(Mathf.DeltaAngle(baseline.Angle, angle));
        if (start < VeerDegrees) return;
        track.VeerBase = baseline.Angle;
        track.VeerAt = now;
        track.VeerMax = start;
        track.Ctx = Open();
    }

    // ---- Context ----

    // Movement the player is holding: a keyboard walk, or the left button held for click-to-move.
    private static bool MovementHeld()
    {
        try
        {
            if (Game.Input is { } input && input.isKeyboardMovementButtonHeld) return true;
        }
        catch { }
        return InputState.Button(0);
    }

    // A hiccup starting now: the state as it begins, then whatever its frames see.
    private static Context Open()
    {
        float now = Time.unscaledTime;
        bool keys = false;
        try { keys = Game.Input?.isKeyboardMovementButtonHeld == true; } catch { }
        string action = ModActions.Last is { } last ? $"'{last}' {Ago(now - ModActions.LastAt)}" : "none";
        var ctx = new Context
        {
            At = now,
            Before = $"{(keys ? "keys" : "mouse")} held, last mod action {action}, last cast {Ago(Cast())}, gc {Ago(now - s_lastGcAt)}",
        };
        Note(ctx);
        return ctx;
    }

    private static void Note(Context ctx)
    {
        ctx.Gate |= InputGate.Blocking;
        ctx.Menu |= InputGate.Menu;
        ctx.Hud |= InputGate.Hud;
        ctx.Unfocused |= !InputState.RealFocused;
        try
        {
            if (Game.Input is { } input)
            {
                ctx.Lock |= input.forceDisableInput;
                ctx.MouseBlock |= input.blockMouseMovementInput;
            }
        }
        catch { }
        if (ModActions.LastAt >= ctx.At - 0.05f)
        {
            ctx.ModAction = true;
            ctx.Action ??= ModActions.Last;
        }
        ctx.Cast |= Cast() < 0.1f;
        ctx.Macro ??= Safe(RunningMacro);
    }

    private static void Record(string what, Context ctx)
    {
        var during = new List<string>();
        if (ctx.Gate) during.Add("mod input lock");
        if (ctx.Lock) during.Add("game input off");
        if (ctx.MouseBlock) during.Add("click-to-move blocked");
        if (ctx.Menu) during.Add("menu open");
        if (ctx.Hud) during.Add("HUD press");
        if (ctx.Unfocused) during.Add("window unfocused");
        if (ctx.ModAction) during.Add($"mod action '{ctx.Action}'");
        if (ctx.Cast) during.Add("skill cast");
        if (ctx.Macro != null) during.Add($"macro '{ctx.Macro}' running");
        if (Time.unscaledTime - s_lastGcAt < 0.5f) during.Add("gc just ran");
        string worst = ctx.WorstFrameMs > 0f ? $", worst frame {ctx.WorstFrameMs.ToString("F0", s_inv)} ms" : string.Empty;
        bool? online = GameState.Online;
        string line = $"{DateTime.Now.ToString("HH:mm:ss.fff", s_inv)} {what}{worst}, scene '{GameState.Scene}' {(online == true ? "online" : online == false ? "offline" : "mode ?")}; "
            + $"before: {ctx.Before}; during: {(during.Count > 0 ? string.Join(", ", during) : "nothing from the mod")}";
        s_kept.Enqueue(line);
        while (s_kept.Count > Kept) s_kept.Dequeue();
        try { Recorded?.Invoke(line); } catch { }
    }

    private static float Cast() => Safe(SecondsSinceCast) ?? float.PositiveInfinity;

    private static string Ago(float seconds) => float.IsInfinity(seconds) || float.IsNaN(seconds) ? "never" : $"{seconds.ToString("F2", s_inv)} s ago";

    private static T? Safe<T>(Func<T?>? hook)
    {
        try { return hook == null ? default : hook(); } catch { return default; }
    }
}
