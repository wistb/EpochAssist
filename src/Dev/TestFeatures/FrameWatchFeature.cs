using System.Globalization;
using EpochAssist.Core;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: writes dev/frames.log, with the time of day on every line: each movement hiccup the movement watch records
/// (the same lines the diagnostics copy holds), every frame that took too long, and a mark line for F9 or `frames mark`,
/// so a stutter felt in game can be matched to what happened at that moment. Nothing is written while the game loads.
/// </summary>
internal sealed class FrameWatchFeature : Feature
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // A frame this long is noted; a normal frame is under 20 ms.
    private const float SlowMs = 50f;
    private const KeyCode MarkKey = KeyCode.F9;
    private const int Kept = 40;

    private static StreamWriter? s_out;
    private static readonly Queue<string> s_recent = new();
    private static int s_slow, s_marks;
    private static float s_worstMs;

    public override string Id => "framewatch";
    public override string Name => "Frame watch";

    public static string FilePath => Path.Combine(DevBridge.Dir, "frames.log");

    public override void Start()
    {
        s_slow = s_marks = 0;
        s_worstMs = 0f;
        s_recent.Clear();
        try
        {
            Directory.CreateDirectory(DevBridge.Dir);
            s_out = new StreamWriter(FilePath, append: true) { AutoFlush = true };
        }
        catch (Exception e)
        {
            s_out = null;
            Log.Warn($"Frame watch: can't write {FilePath}: {e.Message}");
        }
        Write($"--- start: build {Runtime.BuildId}, scene '{GameState.ActiveSceneName}'");
        MovementWatch.Recorded += OnHiccup;
        Commands.Register(new Command("frames", "frames [mark [text]|clear]",
            "Shows the frame watch: movement hiccups, slow frames and marks (dev/frames.log) and the mod's recent actions, adds a mark, or clears the counts.",
            CommandMark.Dev, Id, Frames));
        Actions.Register(Id, "framewatch.mark", "Frame watch: mark", () => Mark("hotkey"));
    }

    public override void Update()
    {
        if (InputState.KeyDown(MarkKey)) Mark("F9");
        if (GameState.Loading) return;
        float ms = Time.unscaledDeltaTime * 1000f;
        if (ms < SlowMs) return;
        s_slow++;
        s_worstMs = Math.Max(s_worstMs, ms);
        Write($"frame {Time.frameCount}: slow {ms.ToString("F0", s_inv)} ms, scene '{GameState.ActiveSceneName}'");
    }

    public override void Shutdown()
    {
        MovementWatch.Recorded -= OnHiccup;
        Write("--- stop");
        try { s_out?.Dispose(); } catch { }
        s_out = null;
    }

    // The watch's line carries its own time of day.
    private static void OnHiccup(string line) => WriteRaw(line);

    private static void Mark(string from)
    {
        s_marks++;
        Write($"MARK #{s_marks} ({from})");
        Toasts.Show("framewatch", $"Frame watch: mark #{s_marks}", ToastKind.Info);
    }

    private static void Write(string text) => WriteRaw($"{DateTime.Now.ToString("HH:mm:ss.fff", s_inv)} {text}");

    private static void WriteRaw(string line)
    {
        s_recent.Enqueue(line);
        while (s_recent.Count > Kept) s_recent.Dequeue();
        try { s_out?.WriteLine(line); } catch { }
    }

    private static CommandResult Frames(CommandCall call)
    {
        switch (call.Lower(1))
        {
            case "mark":
                s_marks++;
                Write($"MARK #{s_marks} (bridge){(call.Args.Length > 1 ? " " + call.Rest(2) : string.Empty)}");
                return CommandResult.Ok($"frames: mark #{s_marks}");
            case "clear":
                s_slow = s_marks = 0;
                s_worstMs = 0f;
                s_recent.Clear();
                MovementWatch.Reset();
                ModActions.Clear();
                Write("--- counts cleared");
                return CommandResult.Ok("frames: counts cleared");
            case "":
                var lines = new List<string>
                {
                    $"frames: movement {MovementWatch.Summary} ({MovementWatch.Seen}); slow (≥{SlowMs:F0} ms) {s_slow}, worst {s_worstMs.ToString("F0", s_inv)} ms, marks {s_marks}; file {FilePath}",
                };
                lines.AddRange(s_recent);
                lines.Add("recent mod actions:");
                lines.AddRange(ModActions.Lines().Select(l => "  " + l));
                return CommandResult.Ok(string.Join("\n", lines));
            default:
                return CommandResult.Error("usage: frames [mark [text]|clear]");
        }
    }
}
