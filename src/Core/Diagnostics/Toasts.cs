using UnityEngine;

namespace EpochAssist.Core;

// Runtime only, never stored.
internal enum ToastKind { Info, Warning }

/// <summary>A shown toast; a sticky one stays until the player dismisses it (its ×, or doing what it asks).</summary>
internal sealed record Toast(string Source, string Text, ToastKind Kind, float ShownAt, bool Sticky = false);

/// <summary>
/// Short messages any feature can show; the toasts feature in the UI draws them. The same source and text show at most
/// once per 10 s, a few are on screen at once (a new one replaces the oldest), and each goes after a few seconds; the
/// player's Notifications settings set how many, how long and which kinds, through the hooks the toast view sets. Nothing
/// is stored; every toast is also logged, shown or not. A sticky toast is for something the player must acknowledge: it
/// shows whatever the kinds setting says, never times out or is pushed out, and goes when dismissed.
/// </summary>
internal static class Toasts
{
    public const float RepeatSeconds = 10f;
    public const float DefaultShowSeconds = 6f;
    public const float FadeSeconds = 0.3f;
    public const int DefaultMaxShown = 3;

    private static readonly List<Toast> s_shown = new();
    private static readonly Dictionary<(string Source, string Text), float> s_lastShown = new();

    /// <summary>The player's duration, stack limit and kinds shown, set by the toast view while it runs.</summary>
    public static Func<float>? ShowSecondsSetting { get; set; }
    public static Func<int>? MaxShownSetting { get; set; }
    public static Func<ToastKind, bool>? ShowsKind { get; set; }

    /// <summary>Seconds a toast stays before it fades.</summary>
    public static float ShowSeconds => Mathf.Clamp(ShowSecondsSetting?.Invoke() ?? DefaultShowSeconds, 0.5f, 60f);

    /// <summary>Toasts on screen at once.</summary>
    public static int MaxShown => Math.Clamp(MaxShownSetting?.Invoke() ?? DefaultMaxShown, 1, 20);

    /// <summary>Shown toasts, oldest first, at most MaxShown (sticky ones always); on the unscaled clock, like every time
    /// here.</summary>
    public static IReadOnlyList<Toast> Visible
    {
        get
        {
            Expire(Time.unscaledTime);
            // The limit can be lowered while toasts are up: the newest stay.
            TrimTo(MaxShown);
            return s_shown;
        }
    }

    /// <summary>Where the shown sticky toasts' dismiss marks are, in screen pixels (top-left origin), set by the toast
    /// view; the HUD's hit test keeps those clicks from the game.</summary>
    public static IReadOnlyList<Rect> DismissAreas { get; set; } = Array.Empty<Rect>();

    /// <summary>Shows a toast; the same text from the same source within RepeatSeconds is dropped unless repeats is set
    /// (each one is news, as a debug trace's).</summary>
    public static void Show(string source, string text, ToastKind kind, bool repeats = false, bool sticky = false)
    {
        float now = Time.unscaledTime;
        var key = (source, text);
        // Still waiting for its dismiss: once is enough.
        if (sticky && s_shown.Any(t => t.Sticky && t.Source == source && t.Text == text)) return;
        if (!repeats && s_lastShown.TryGetValue(key, out float at) && now - at < RepeatSeconds) return;
        foreach (var old in s_lastShown.Where(p => now - p.Value >= RepeatSeconds).Select(p => p.Key).ToList()) s_lastShown.Remove(old);
        s_lastShown[key] = now;

        if (sticky || ShowsKind?.Invoke(kind) != false)
        {
            Expire(now);
            TrimTo(MaxShown - 1);
            s_shown.Add(new Toast(source, text, kind, now, sticky));
        }
        Log.Info($"toast ({source}{(sticky ? ", until dismissed" : string.Empty)}): {text}");
    }

    /// <summary>Takes a sticky toast down (its × clicked).</summary>
    public static void Dismiss(Toast toast)
    {
        if (s_shown.Remove(toast)) Log.Info($"toast ({toast.Source}) dismissed");
    }

    /// <summary>Takes a source's sticky toasts down (the player did what they ask).</summary>
    public static void Dismiss(string source)
    {
        foreach (var toast in s_shown.Where(t => t.Sticky && t.Source == source).ToList()) Dismiss(toast);
    }

    /// <summary>How far a toast has faded: 1 while shown, down to 0 over its last moments.</summary>
    public static float Alpha(Toast toast, float now) => toast.Sticky ? 1f :
        Mathf.Clamp01((toast.ShownAt + ShowSeconds + FadeSeconds - now) / FadeSeconds);

    /// <summary>Where the toasts were last drawn, in screen pixels (empty when none were), so other overlays keep clear.</summary>
    public static Rect ShownArea { get; set; }

    public static void Clear()
    {
        s_shown.Clear();
        s_lastShown.Clear();
        DismissAreas = Array.Empty<Rect>();
    }

    private static void Expire(float now) => s_shown.RemoveAll(t => !t.Sticky && now - t.ShownAt >= ShowSeconds + FadeSeconds);

    // The oldest timed toasts go first; sticky ones wait for their dismiss.
    private static void TrimTo(int count)
    {
        while (s_shown.Count > count && s_shown.FindIndex(t => !t.Sticky) is int oldest and >= 0) s_shown.RemoveAt(oldest);
    }
}
