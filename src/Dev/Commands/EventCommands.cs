using System.Globalization;
using EpochAssist.Core;

namespace EpochAssist.Dev;

// jobs: the scheduler's jobs and the dispatcher's send rate.
internal static class EventCommands
{
    public const string JobsUsage = "jobs [reset]";

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // jobs [reset]: every feature's jobs, the host jobs, then the presses; reset clears the press peak and total first.
    public static CommandResult Jobs(CommandCall call)
    {
        if (call.Lower(1) == "reset") Dispatcher.ResetPressCounts();
        else if (call.Args.Length > 0) return CommandResult.Error($"jobs: usage {JobsUsage}");

        var rows = new List<string>();
        int featureJobs = 0;
        foreach (var feature in FeatureHost.All)
        {
            foreach (var job in feature.Record.Jobs)
            {
                if (!job.Pending) continue;
                featureJobs++;
                rows.Add(JobLine(feature.Id, job));
            }
        }
        int hostJobs = 0;
        foreach (var job in Scheduler.HostJobs)
        {
            if (!job.Pending) continue;
            hostJobs++;
            rows.Add(JobLine("host", job));
        }
        var lines = new List<string> { $"ok jobs: {featureJobs} jobs, {hostJobs} host job{(hostJobs == 1 ? string.Empty : "s")}, t={Clock.Now.ToString("F2", s_inv)}" };
        lines.AddRange(rows);
        lines.Add(PressesLine());
        lines.Add(SlotsLine());
        return new CommandResult(DevOutput.Reply("jobs", lines));
    }

    private static string JobLine(string owner, Job job)
    {
        string when = job.Kind switch
        {
            JobKind.Repeat => $"every {Sec(job.Interval)}".PadRight(14) + $"next {Sec(Math.Max(0f, job.DueAt - Clock.Now))}".PadRight(12),
            JobKind.Delay => "after".PadRight(14) + $"due {Sec(Math.Max(0f, job.DueAt - Clock.Now))}".PadRight(12),
            _ => "spread".PadRight(14) + $"slice {Ms(job.LastSliceMs)}".PadRight(12),
        };
        string tail = job.Kind == JobKind.Spread ? $" overruns {job.Overruns}" : string.Empty;
        return $"{owner.PadRight(10)} {job.Name.PadRight(14)} {when} runs {job.Runs.ToString(s_inv).PadRight(6)} last {Ms(job.LastMs)}{tail}";
    }

    private static string Sec(float seconds) => seconds.ToString("F2", s_inv) + "s";

    private static string Ms(double ms) => ms.ToString("F3", s_inv) + "ms";

    public const string EventsUsage = "events [count] | events subs | events watch <seconds>|stop";

    // events [count]: the last events raised (20 when not given); events subs: subscriptions by owner, then producers;
    // events watch: every event into events.txt for a while.
    public static CommandResult Events(CommandCall call)
    {
        if (call.Lower(1) == "subs") return Subs();
        if (call.Lower(1) == "watch") return EventWatch(call);
        int count = 20;
        if (call.Args.Length > 0 && (!int.TryParse(call.Lower(1), out count) || count < 1)) return CommandResult.Error($"events: usage {EventsUsage}");
        var recent = Bus.Recent.ToList();
        var lines = new List<string> { $"ok events: last {Math.Min(count, recent.Count)} of {Bus.RaisedCount}" };
        lines.AddRange(recent.Skip(Math.Max(0, recent.Count - count)).Select(e => $"{e.At:HH:mm:ss.fff} {e.Text}"));
        return new CommandResult(DevOutput.Reply("events", lines));
    }

    private static CommandResult Subs()
    {
        var byOwner = Bus.Channels.SelectMany(c => c.Subscriptions.Where(s => s.Active))
            .GroupBy(s => s.Owner.Id)
            .Select(g => $"{g.Key.PadRight(10)} {string.Join(", ", g.Select(s => s.Type.Name).Distinct())}")
            .ToList();
        int total = Bus.Channels.Sum(c => c.Subscriptions.Count(s => s.Active));
        var lines = new List<string> { $"ok events subs: {total} subscription{(total == 1 ? string.Empty : "s")}" };
        lines.AddRange(byOwner);
        lines.Add("producers  " + ProducersLine());
        return new CommandResult(DevOutput.Reply("events", lines));
    }

    private static string ProducersLine()
    {
        bool? online = Player.Online;
        return string.Join(", ", GameEvents.Producers.Select(p =>
        {
            if (online is bool mode && !p.Supports(mode)) return $"{p.Part} off (not supported {(mode ? "online" : "offline")})";
            string state = p.Active ? "on" : "off";
            return $"{p.Part} {state}";
        }));
    }

    // ---- events watch ----

    private static readonly List<Subscription> s_watch = new();
    private static StreamWriter? s_watchFile;
    private static Job? s_watchEnd;
    private static float s_watchStart;
    private static int s_watchLines;

    private static string WatchPath => Path.Combine(DevBridge.Dir, "events.txt");

    // The dev bridge feature subscribes to every event, so each producer runs while the watch lasts.
    private static CommandResult EventWatch(CommandCall call)
    {
        if (call.Lower(2) == "stop")
        {
            int lines = s_watchLines;
            bool running = s_watchFile != null;
            StopWatch("stopped");
            return CommandResult.Ok(running ? $"events watch stop: {lines} events in events.txt" : "events watch stop: no watch running");
        }
        if (!float.TryParse(call.Lower(2), NumberStyles.Float, s_inv, out float seconds) || seconds <= 0f)
            return CommandResult.Error("events watch: usage events watch <seconds>|stop");
        if (FeatureHost.Find("dev") is not { State: FeatureState.Running } dev) return CommandResult.Error("events watch: the dev bridge isn't running");
        StopWatch("replaced by a new watch");
        Directory.CreateDirectory(DevBridge.Dir);
        s_watchFile = new StreamWriter(WatchPath, append: false) { AutoFlush = true };
        s_watchStart = Clock.Now;
        s_watchLines = 0;
        s_watchFile.WriteLine($"# events watch {seconds.ToString("F0", s_inv)} s, build {Runtime.BuildId}, scene {GameState.Scene}, started {DateTime.Now:HH:mm:ss}");
        Watch<ZoneChange>(dev);
        Watch<PlayerSpawned>(dev);
        Watch<PlayerDied>(dev);
        Watch<HitTaken>(dev);
        Watch<XpGained>(dev);
        Watch<GoldChanged>(dev);
        Watch<ItemDropped>(dev);
        Watch<DeathCause>(dev);
        Watch<EchoCompleted>(dev);
        Watch<MonolithPick>(dev);
        s_watchEnd = Scheduler.After(dev, "events watch", seconds, () => StopWatch("time is up"));
        return CommandResult.Ok($"events watch {seconds.ToString("F0", s_inv)}s: every event into events.txt");
    }

    private static void Watch<T>(Feature dev) where T : struct =>
        s_watch.Add(Bus.Subscribe<T>(dev, e =>
        {
            s_watchLines++;
            s_watchFile?.WriteLine($"t={(Clock.Now - s_watchStart).ToString("F3", s_inv)} f={UnityEngine.Time.frameCount} {e}");
        }));

    /// <summary>Ends a running watch (also when the dev bridge stops).</summary>
    public static void StopWatch(string reason)
    {
        foreach (var subscription in s_watch) subscription.Dispose();
        s_watch.Clear();
        s_watchEnd?.Cancel();
        s_watchEnd = null;
        if (s_watchFile == null) return;
        s_watchFile.WriteLine($"# end: {reason}, {s_watchLines} events");
        s_watchFile.Dispose();
        s_watchFile = null;
    }

    private static string PressesLine() =>
        $"presses    last 1s {Dispatcher.PressesLastSecond}, peak {Dispatcher.PeakPressesPerSecond}/s, total {Dispatcher.PressesSinceReset} since reset, cap {Dispatcher.MaxPressesPerSecond}/s";

    private static string SlotsLine() =>
        "slots      " + string.Join("  ", Enumerable.Range(0, Dispatcher.SlotCount).Select(SlotPressText));

    // "2: 41 presses, spacing mean 1.42 s min 0.61 s" since the last reset.
    private static string SlotPressText(int i)
    {
        int n = Dispatcher.SlotPresses[i];
        if (n < 2) return $"{i + 1}: {n}";
        return $"{i + 1}: {n}, spacing mean {(Dispatcher.SlotGapSum[i] / (n - 1)).ToString("F2", s_inv)} s min {Dispatcher.SlotMinGap[i].ToString("F2", s_inv)} s";
    }
}
