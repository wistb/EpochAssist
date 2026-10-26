using System.Collections.Concurrent;
using System.Text;

namespace EpochAssist.Core;

/// <summary>
/// Writes session history off the game's frame: work handed over (appends, exports, closing old sessions, rotation)
/// runs in order on one background thread that touches only .NET file APIs, never Unity or game objects. A failed
/// append is kept and tried again with that file's next batch; the error shows in Diagnostics until one succeeds. Stop
/// lets the queue finish and joins the thread (at most 1 s), so a reload leaves no thread behind. With Synchronous
/// (set by a dev command) work runs on the caller instead.
/// </summary>
internal static class SessionWriter
{
    private const int JoinMs = 1000;
    private const int WaitMs = 1000;
#if DEV
    private const string ThreadsKey = "EpochAssist.SessionWriterThreads";
#endif

    private static readonly object s_lock = new();
    private static readonly Queue<Action> s_work = new();
    // Text that couldn't be appended yet, by file; only the thread doing the work touches it.
    private static readonly Dictionary<string, StringBuilder> s_failed = new(StringComparer.OrdinalIgnoreCase);
    // Lines for the main thread to log (the log isn't written from the writer's thread).
    private static readonly ConcurrentQueue<(bool Warn, string Text)> s_messages = new();
    private static Thread? s_thread;
    private static bool s_stopping;
    private static volatile string? s_lastError;

    /// <summary>Dev only: run work on the caller, for the synchronous fallback's measurement.</summary>
    public static bool Synchronous { get; set; }

    /// <summary>Why the last write failed, or null once one succeeded again.</summary>
    public static string? LastError => s_lastError;

    public static void Start()
    {
        lock (s_lock)
        {
            if (s_thread != null) return;
            s_stopping = false;
            s_thread = new Thread(Loop) { IsBackground = true, Name = "Epoch Assist session writer" };
            s_thread.Start();
        }
    }

    /// <summary>Appends text (whole lines, each ending in \n) to a session file.</summary>
    public static void Append(string path, string text) => Post(() => Write(path, text));

    /// <summary>Runs work in order with the appends; on the caller when synchronous or not started.</summary>
    public static void Post(Action work)
    {
        lock (s_lock)
        {
            if (!Synchronous && s_thread != null && !s_stopping)
            {
                s_work.Enqueue(work);
                Monitor.Pulse(s_lock);
                return;
            }
        }
        Run(work);
    }

    /// <summary>With wait, blocks until everything handed over so far is written (at most 1 s); false when it wasn't.</summary>
    public static bool Flush(bool wait)
    {
        if (!wait) return true;
        using var done = new ManualResetEventSlim(false);
        Post(done.Set);
        return done.Wait(WaitMs);
    }

    /// <summary>Finishes the queue and joins the thread (at most 1 s); logs if it didn't end.</summary>
    public static void Stop()
    {
        Thread? thread;
        lock (s_lock)
        {
            thread = s_thread;
            s_stopping = true;
            Monitor.PulseAll(s_lock);
        }
        if (thread != null && !thread.Join(JoinMs)) Log.Warn("Session writer thread didn't finish within 1 s; it ends with the game");
        lock (s_lock)
        {
            s_thread = null;
            s_stopping = false;
        }
        DrainMessages();
    }

    /// <summary>Writes the writer's messages to the log (main thread, once a frame).</summary>
    public static void DrainMessages()
    {
        while (s_messages.TryDequeue(out var message))
        {
            if (message.Warn) Log.Warn(message.Text);
            else Log.Info(message.Text);
        }
    }

    /// <summary>For work on the writer's thread: a line the main thread logs at Info.</summary>
    public static void Tell(string message) => s_messages.Enqueue((false, message));

#if DEV
    /// <summary>Dev only: writer threads alive in the process, from every copy of the mod loaded so far.</summary>
    public static int LiveThreads => AppDomain.CurrentDomain.GetData(ThreadsKey) is int n ? n : 0;

    public static bool ThreadAlive => s_thread?.IsAlive ?? false;

    private static void Count(int change)
    {
        lock (AppDomain.CurrentDomain) AppDomain.CurrentDomain.SetData(ThreadsKey, LiveThreads + change);
    }
#endif

    private static void Loop()
    {
#if DEV
        Count(1);
#endif
        try
        {
            while (true)
            {
                Action work;
                lock (s_lock)
                {
                    while (s_work.Count == 0 && !s_stopping) Monitor.Wait(s_lock);
                    if (s_work.Count == 0) return;
                    work = s_work.Dequeue();
                }
                Run(work);
            }
        }
        finally
        {
#if DEV
            Count(-1);
#endif
        }
    }

    private static void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception e)
        {
            s_lastError = e.Message;
            s_messages.Enqueue((true, $"Sessions: {e.Message}"));
        }
    }

    // Text that failed before goes first, so lines stay in order.
    private static void Write(string path, string text)
    {
        if (s_failed.TryGetValue(path, out var failed)) text = failed.Append(text).ToString();
        try
        {
            SessionFiles.Append(path, text);
            s_failed.Remove(path);
            s_lastError = null;
        }
        catch (Exception e)
        {
            s_failed[path] = new StringBuilder(text);
            if (s_lastError != e.Message) s_messages.Enqueue((true, $"Sessions not written: {e.Message}; tried again with the next batch"));
            s_lastError = e.Message;
        }
    }
}
