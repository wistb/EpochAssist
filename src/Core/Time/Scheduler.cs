using UnityEngine;

namespace EpochAssist.Core;

/// <summary>The mod's one clock: unscaled seconds, so the game's time stop and pause menu don't freeze timers.</summary>
internal static class Clock
{
    public static float Now => Time.unscaledTime;
}

/// <summary>
/// A "not before" time on the clock, for checks that run where their result is needed (a lookup retry). It registers
/// nothing, so it needs no owner and no cleanup.
/// </summary>
internal struct Cooldown
{
    /// <summary>Clock time it is ready at; 0 is ready.</summary>
    public float ReadyAt;

    public readonly bool Ready => Clock.Now >= ReadyAt;

    /// <summary>Seconds until ready, 0 when ready.</summary>
    public readonly float Left => Math.Max(0f, ReadyAt - Clock.Now);

    public void Start(float seconds) => ReadyAt = Clock.Now + seconds;

    public void Clear() => ReadyAt = 0f;
}

internal enum JobKind { Delay, Repeat, Spread }

/// <summary>
/// Work the scheduler runs for a feature (or the host): once after a delay, again and again at an interval, or a little
/// each frame within a time slice. It runs only while its owner is Running and is dropped when the owner stops,
/// faults, becomes unavailable or the mod reloads, so Shutdown never has to cancel it.
/// </summary>
internal sealed class Job
{
    private readonly Action? _action;

    internal Job(Feature? owner, string name, JobKind kind, float interval, Action? action, Func<double>? slice, Func<bool>? step)
    {
        Owner = owner;
        Name = name;
        Kind = kind;
        Interval = interval;
        _action = action;
        Slice = slice;
        Step = step;
        Phase = (kind == JobKind.Spread ? "spread " : "job ") + name;
        // Made once, so a pass allocates nothing.
        Run = kind == JobKind.Spread ? () => Scheduler.Steps(this) : Execute;
    }

    /// <summary>The feature it belongs to, or null for a host job.</summary>
    public Feature? Owner { get; }
    public string Name { get; }
    public JobKind Kind { get; }
    /// <summary>Repeat: seconds from one run to the next due time; delay: the delay it was made with.</summary>
    public float Interval { get; }
    /// <summary>Delay and repeat: when it next runs; 0 is at the owner's next job pass.</summary>
    public float DueAt { get; internal set; }
    /// <summary>False once a delay ran, or after Cancel or the owner's drop; it never runs again.</summary>
    public bool Pending { get; private set; } = true;
    public int Runs { get; private set; }
    public double LastMs { get; private set; }
    /// <summary>Spread: frames where one step alone went past the slice.</summary>
    public int Overruns { get; internal set; }
    /// <summary>Spread: the slice it had in the last frame, in milliseconds.</summary>
    public double LastSliceMs { get; internal set; }

    internal readonly Func<double>? Slice;
    internal readonly Func<bool>? Step;
    // The guard's phase, made once ("job slot refresh"), and what the guard runs.
    internal readonly string Phase;
    internal readonly Action Run;

    public void Cancel() => Pending = false;

    /// <summary>Due at the owner's next job pass (the start of its next Update).</summary>
    public void Due()
    {
        if (Pending && Kind != JobKind.Spread) DueAt = 0f;
    }

    /// <summary>Runs it now, in the caller's guard; a repeat's interval starts again from now.</summary>
    public void RunNow()
    {
        if (Pending && Kind != JobKind.Spread) Execute();
    }

    // The next due time is set before the action runs, so a failed repeat waits one interval, a failed delay is done,
    // and an action may make its own job due again.
    internal void Execute()
    {
        if (Kind == JobKind.Delay) Pending = false;
        else DueAt = Clock.Now + Interval;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _action!();
        }
        finally
        {
            Record(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    internal void Record(long ticks)
    {
        Runs++;
        LastMs = FrameTimes.Ms(ticks);
    }
}

/// <summary>
/// The host's timers: a feature's due delay and repeat jobs run just before its own Update, its
/// spread jobs' steps just after, each in that feature's guard; host jobs (the saves) run after every LateUpdate. One
/// clock, Clock.Now; a repeat is next due one interval after it ran, so a long frame or a loading screen gives one run,
/// never a burst.
/// </summary>
internal static class Scheduler
{
    private static readonly List<Job> s_hostJobs = new();
    private static readonly Predicate<Job> s_done = job => !job.Pending;

    public static IReadOnlyList<Job> HostJobs => s_hostJobs;

    /// <summary>Runs the action once, in the first update at or after the delay.</summary>
    public static Job After(Feature owner, string name, float seconds, Action action) =>
        Add(owner, new Job(owner, name, JobKind.Delay, seconds, action, null, null) { DueAt = Clock.Now + seconds });

    /// <summary>Runs the action every interval: at once with startNow, otherwise first after one interval.</summary>
    public static Job Every(Feature owner, string name, float interval, Action action, bool startNow = false) =>
        Add(owner, new Job(owner, name, JobKind.Repeat, interval, action, null, null) { DueAt = startNow ? 0f : Clock.Now + interval });

    /// <summary>Each frame after the owner's Update, calls step until it returns false (nothing more this frame) or
    /// sliceMs milliseconds are spent.</summary>
    public static Job Spread(Feature owner, string name, Func<double> sliceMs, Func<bool> step) =>
        Add(owner, new Job(owner, name, JobKind.Spread, 0f, null, sliceMs, step));

    /// <summary>A host job: runs once after the delay, after every feature's LateUpdate, whatever state the features
    /// are in; until Runtime.Shutdown.</summary>
    public static Job HostAfter(string name, float seconds, Action action)
    {
        var job = new Job(null, name, JobKind.Delay, seconds, action, null, null) { DueAt = Clock.Now + seconds };
        s_hostJobs.Add(job);
        return job;
    }

    private static Job Add(Feature owner, Job job)
    {
        owner.Record.Jobs.Add(job);
        return job;
    }

    // Jobs added during a pass wait for the next one; cancelled ones are skipped and dropped after it.
    internal static void RunDue(Feature owner)
    {
        var jobs = owner.Record.Jobs;
        int count = jobs.Count;
        if (count == 0) return;
        float now = Clock.Now;
        for (int i = 0; i < count && i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (job.Kind == JobKind.Spread || !job.Pending || job.DueAt > now) continue;
            FeatureHost.Guarded(owner, job.Phase, job.Run);
        }
        jobs.RemoveAll(s_done);
    }

    internal static void RunSpread(Feature owner)
    {
        var jobs = owner.Record.Jobs;
        int count = jobs.Count;
        for (int i = 0; i < count && i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (job.Kind != JobKind.Spread || !job.Pending) continue;
            FeatureHost.Guarded(owner, job.Phase, job.Run);
        }
    }

    internal static void Steps(Job job)
    {
        double slice = job.Slice!();
        job.LastSliceMs = slice;
        if (slice <= 0d) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budget = (long)(slice * System.Diagnostics.Stopwatch.Frequency / 1000d);
        bool overran = false;
        try
        {
            while (System.Diagnostics.Stopwatch.GetTimestamp() - start < budget)
            {
                long stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                bool more = job.Step!();
                if (System.Diagnostics.Stopwatch.GetTimestamp() - stepStart > budget) overran = true;
                if (!more) break;
            }
        }
        finally
        {
            if (overran) job.Overruns++;
            job.Record(System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
    }

    /// <summary>Drops every job of the feature (the host, wherever it removes the feature's actions).</summary>
    internal static void RemoveOwner(Feature owner)
    {
        foreach (var job in owner.Record.Jobs) job.Cancel();
        owner.Record.Jobs.Clear();
    }

    /// <summary>Host jobs that are due; a failure is logged (throttled) and can't fault anything.</summary>
    internal static void RunHostJobs()
    {
        int count = s_hostJobs.Count;
        if (count == 0) return;
        float now = Clock.Now;
        for (int i = 0; i < count && i < s_hostJobs.Count; i++)
        {
            var job = s_hostJobs[i];
            if (!job.Pending || job.DueAt > now) continue;
            try
            {
                job.Execute();
            }
            catch (Exception e)
            {
                Log.ErrorThrottled($"job:{job.Name}", $"Host job {job.Name} failed: {e}", 30);
            }
        }
        s_hostJobs.RemoveAll(s_done);
    }

    /// <summary>Drops the host jobs (Runtime.Shutdown, after the final saves).</summary>
    internal static void DropHostJobs()
    {
        foreach (var job in s_hostJobs) job.Cancel();
        s_hostJobs.Clear();
    }
}
