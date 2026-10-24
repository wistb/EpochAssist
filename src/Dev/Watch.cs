using System.Globalization;
using EpochAssist.Core;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace EpochAssist.Dev;

// watch: one bound value sampled 10 times a second (unscaled time) into watch.txt. The target and path are resolved
// once, so Type#n stays on the same object; the run ends on time, a lost target, a scene change, a new watch, watch
// stop or the bridge stopping, always with an end line and the file closed.
internal static class Watch
{
    private const float Interval = 0.1f;
    private const int MaxFailures = 10;

    private sealed class Run
    {
        public object Owner = null!;
        public List<Inspect.Step> Steps = null!;
        public StreamWriter File = null!;
        public float StartedAt, Seconds, NextAt;
        public int SceneHandle, Samples, Failures;
    }

    private static Run? s_run;

    private static string FilePath => Path.Combine(DevBridge.Dir, "watch.txt");

    /// <summary>watch &lt;target&gt; &lt;member.path&gt; [seconds] | watch stop</summary>
    public static string Command(CommandCall call)
    {
        if (call.Lower(1) == "stop" && call.Args.Length == 1)
        {
            if (s_run == null) return "ok watch stop: no watch running";
            int samples = s_run.Samples;
            Stop("stopped");
            return $"ok watch stop: {samples} samples in watch.txt";
        }
        if (call.Args.Length < 2) return "error watch: usage watch <target> <member.path> [seconds] | watch stop";

        string target = call.Args[0], path = call.Args[1];
        float seconds = 10f;
        if (call.Args.Length > 2)
        {
            if (!float.TryParse(call.Args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) return $"error watch: bad seconds {call.Args[2]}";
            seconds = Mathf.Clamp(seconds, 1f, 120f);
        }

        var (start, error) = Inspect.Target(target);
        if (error != null) return $"error watch {target}: {error}";
        if (!Inspect.ParsePath(path, out var steps, out error)) return $"error watch {target}: {error}";
        if (steps.Any(s => s.All)) return $"error watch {target} {path}: watch reads one value; use [i], not [*]";
        var (owner, ownerError) = Inspect.Owner(start!, steps);
        if (ownerError != null) return $"error watch {target} {path}: {ownerError}";
        string? first = Inspect.ReadLast(owner!, steps, out error);
        if (error != null) return $"error watch {target} {path}: {error}";

        if (s_run != null) Stop("replaced by a new watch");
        Directory.CreateDirectory(DevBridge.Dir);
        var file = new StreamWriter(FilePath, append: false);
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        file.WriteLine($"watch {target} {path} for {seconds.ToString("0.#", CultureInfo.InvariantCulture)} s at 10/s");
        file.WriteLine($"bound: {Inspect.TypeName(owner!.GetType())}{(owner is UnityEngine.Object u ? $" '{u.name}'" : string.Empty)}");
        file.WriteLine($"mode {(Player.Online switch { true => "online", false => "offline", null => "unknown" })}, scene {scene.name}, build {Runtime.BuildId}");
        file.WriteLine($"started {Time.unscaledTime.ToString("F2", CultureInfo.InvariantCulture)} s unscaled, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        s_run = new Run
        {
            Owner = owner, Steps = steps, File = file, StartedAt = Time.unscaledTime, Seconds = seconds,
            NextAt = Time.unscaledTime, SceneHandle = scene.handle,
        };
        Sample(s_run);
        return $"ok watch {target} {path} for {seconds.ToString("0.#", CultureInfo.InvariantCulture)} s at 10/s into watch.txt (first value {first})";
    }

    /// <summary>Called by the dev feature every update, before the bridge.</summary>
    public static void Update()
    {
        if (s_run is not { } run || Time.unscaledTime < run.NextAt) return;
        if (Time.unscaledTime - run.StartedAt >= run.Seconds)
        {
            Stop("time up");
            return;
        }
        if (run.Owner is Il2CppObjectBase obj && !Game.Alive(obj))
        {
            Stop("target gone");
            return;
        }
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle != run.SceneHandle)
        {
            Stop("scene changed");
            return;
        }
        Sample(run);
    }

    /// <summary>Ends the running watch, if any, with its end line, and closes the file.</summary>
    public static void Stop(string reason)
    {
        if (s_run is not { } run) return;
        s_run = null;
        try
        {
            run.File.WriteLine($"end: {reason}, {run.Samples} samples");
        }
        finally
        {
            run.File.Dispose();
        }
    }

    private static void Sample(Run run)
    {
        // One sample a frame at most; after a long frame the next is due one interval on, not in a burst.
        run.NextAt = Math.Max(run.NextAt + Interval, Time.unscaledTime);
        string t = (Time.unscaledTime - run.StartedAt).ToString("F2", CultureInfo.InvariantCulture);
        string? value = Inspect.ReadLast(run.Owner, run.Steps, out string? error);
        run.Samples++;
        run.File.WriteLine($"t={t} {value ?? error}");
        run.File.Flush();
        run.Failures = error == null ? 0 : run.Failures + 1;
        if (run.Failures >= MaxFailures) Stop($"read failed: {error}");
    }
}
