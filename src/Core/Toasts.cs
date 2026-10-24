using UnityEngine;

namespace EpochAssist.Core;

// Runtime only, never stored.
internal enum ToastKind { Info, Warning }

internal sealed record Toast(string Source, string Text, ToastKind Kind, float ShownAt);

/// <summary>
/// Short messages any feature can show; the toasts feature in the UI draws them. The same source and text show at most
/// once per 10 s, at most three are on screen (a new one replaces the oldest), and each goes after about 4 s. Nothing is
/// stored; every toast is also logged.
/// </summary>
internal static class Toasts
{
    public const float RepeatSeconds = 10f;
    public const float ShowSeconds = 4f;
    public const float FadeSeconds = 0.3f;
    private const int MaxShown = 3;

    private static readonly List<Toast> s_shown = new();
    private static readonly Dictionary<(string Source, string Text), float> s_lastShown = new();

    /// <summary>Shown toasts, oldest first; on the unscaled clock, like every time here.</summary>
    public static IReadOnlyList<Toast> Visible
    {
        get
        {
            Expire(Time.unscaledTime);
            return s_shown;
        }
    }

    public static void Show(string source, string text, ToastKind kind)
    {
        float now = Time.unscaledTime;
        var key = (source, text);
        if (s_lastShown.TryGetValue(key, out float at) && now - at < RepeatSeconds) return;
        foreach (var old in s_lastShown.Where(p => now - p.Value >= RepeatSeconds).Select(p => p.Key).ToList()) s_lastShown.Remove(old);
        s_lastShown[key] = now;

        Expire(now);
        if (s_shown.Count >= MaxShown) s_shown.RemoveAt(0);
        s_shown.Add(new Toast(source, text, kind, now));
        Log.Info($"toast ({source}): {text}");
    }

    /// <summary>How far a toast has faded: 1 while shown, down to 0 over its last moments.</summary>
    public static float Alpha(Toast toast, float now) =>
        Mathf.Clamp01((toast.ShownAt + ShowSeconds + FadeSeconds - now) / FadeSeconds);

    public static void Clear()
    {
        s_shown.Clear();
        s_lastShown.Clear();
    }

    private static void Expire(float now) => s_shown.RemoveAll(t => now - t.ShownAt >= ShowSeconds + FadeSeconds);
}
