using EpochAssist.Core;
using MelonLoader.Utils;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// Dev builds only: runs commands written to UserData/EpochAssist/dev/commands.txt (one per line, one per frame)
/// and reports to result.txt, ending with "done". tools/shot.sh drives it. Commands come from the registry (help lists
/// them); a command's settle frames pass before the next one, and a pending reply (a screenshot) is waited for.
///   release: help [command]   actions   run [action]   set [setting id] [value]   state   status   hotkeys   compat   log [level] [count]
///            theme list|reload|problems
///   dev:     menu open [feature id with a page]|settings [tab]   menu close   menu search [text]   hud panel open|close
///            facts   probe [type] [filter]   find [text]   fonts   wait [frames]   shot full|menu|hud [name]   move [x] [y] [held]   stopmove
///            theme set [name]|check   resources   toast [text]   toasts   travel [scene]   focus   draw test on|off
///            reload   diag   perf [timers on|off]   profile   crash fault|start|throw [n]   reports
/// Screenshots: Unity saves the full screen, then the mod crops it and writes shots/[name].png.
/// </summary>
internal static class DevBridge
{
    private const float PollSeconds = 0.25f;

    private static readonly Queue<string> s_queue = new();
    private static readonly List<string> s_results = new();
    private static float s_nextPollAt;
    private static int s_waitFrames;
    private static (Func<string?> Reply, string Line)? s_pending;
    private static bool s_batchActive;

    /// <summary>A reload asked for by a bridge command waits until the batch's results are written.</summary>
    public static bool ReloadAfterBatch;

    internal static string Dir => Path.Combine(MelonEnvironment.UserDataDirectory, "EpochAssist", "dev");

    public static void Update()
    {
        if (s_pending is { } pending)
        {
            if (Commands.Poll(pending.Reply, pending.Line) is not { } reply) return;
            s_pending = null;
            Report(reply);
            return;
        }
        if (s_waitFrames > 0)
        {
            s_waitFrames--;
            return;
        }
        if (s_queue.Count > 0)
        {
            Execute(s_queue.Dequeue());
            return;
        }
        // The batch is over once its last command, pending reply and settle frames are all through.
        if (s_batchActive)
        {
            Finish();
            return;
        }
        if (Time.unscaledTime < s_nextPollAt) return;
        s_nextPollAt = Time.unscaledTime + PollSeconds;
        Poll();
    }

    public static void Reset()
    {
        s_queue.Clear();
        s_results.Clear();
        s_pending = null;
        s_waitFrames = 0;
        s_batchActive = false;
        ReloadAfterBatch = false;
    }

    private static void Poll()
    {
        string file = Path.Combine(Dir, "commands.txt");
        if (!File.Exists(file)) return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
            File.Delete(file);
        }
        catch (IOException)
        {
            return;
        }
        s_results.Clear();
        foreach (string line in lines)
        {
            if (line.Trim().Length > 0) s_queue.Enqueue(line.Trim());
        }
        s_batchActive = true;
    }

    private static void Execute(string command)
    {
        if (Commands.Run(command, "bridge", hosted: true) is not { } result) return;
        if (result.Text.Length > 0) Report(result.Text);
        s_waitFrames = result.Settle;
        if (result.Pending != null) s_pending = (result.Pending, command);
    }

    // Long replies (log, diag) go to the log by their first line only, so the log doesn't fill with its own lines.
    private static void Report(string text)
    {
        string[] lines = text.Split('\n');
        s_results.AddRange(lines);
        Log.Info("dev: " + lines[0] + (lines.Length > 1 ? $" (+{lines.Length - 1} lines)" : string.Empty));
    }

    private static void Finish()
    {
        s_batchActive = false;
        s_results.Add("done");
        try
        {
            File.WriteAllLines(Path.Combine(Dir, "result.txt"), s_results);
        }
        catch (IOException e)
        {
            Log.Warn($"dev: could not write result.txt: {e.Message}");
        }
        s_results.Clear();
        if (ReloadAfterBatch) Runtime.ReloadRequested = true;
    }
}

internal sealed class DevBridgeFeature : Feature
{
    public override string Id => "dev";
    public override string Name => "Dev bridge";
    // The draw test pattern draws in the world layer.
    public override GuiLayer Layer => GuiLayer.World;

    public override void Start()
    {
        DevCommands.Register(Id);
        CoreCommands.CompatSelfTest = () => new CompatProbeFeature();
    }

    public override void Update() => DevBridge.Update();
    public override void OnGUI() => DevCommands.OnGUI();

    public override void Shutdown()
    {
        CoreCommands.CompatSelfTest = null;
        DevBridge.Reset();
        DevCommands.Reset();
    }
}
