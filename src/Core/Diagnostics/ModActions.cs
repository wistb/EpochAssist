using System.Globalization;
using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The last things the mod did that reach the character or the game's input: skill key-downs and key-ups, channel
/// stops and retargets, drinks, portals, interactions, button presses and quick moves, input locks and movement
/// stops. For the diagnostics copy, and for the movement watch to say what the mod did just before a hiccup. The same
/// action repeated within a short time is one entry with a count, so a key-up sent every frame doesn't push the rest out.
/// </summary>
internal static class ModActions
{
    private const int Kept = 30;
    private const float RepeatSeconds = 0.5f;
    private const int Slots = 6;

    private struct Entry
    {
        public float At;
        public DateTime Clock;
        public string What;
        public int Count;
    }

    private static readonly Entry[] s_ring = new Entry[Kept];
    private static int s_next, s_stored;

    // Made once, so the per-frame sends allocate nothing.
    private static readonly string[] s_keyDown = Enumerable.Range(1, Slots).Select(n => $"skill {n} key-down").ToArray();
    private static readonly string[] s_keyUp = Enumerable.Range(1, Slots).Select(n => $"skill {n} key-up").ToArray();
    private static readonly string[] s_stop = Enumerable.Range(1, Slots).Select(n => $"skill {n} channel stop").ToArray();

    /// <summary>When the last action was (unscaled seconds), and what it was; negative infinity and null before any.</summary>
    public static float LastAt { get; private set; } = float.NegativeInfinity;
    public static string? Last { get; private set; }

    public static void SkillKey(int slot, bool down) => Note(slot is >= 0 and < Slots ? (down ? s_keyDown[slot] : s_keyUp[slot]) : down ? "skill key-down" : "skill key-up");

    public static void ChannelStop(int slot) => Note(slot is >= 0 and < Slots ? s_stop[slot] : "channel stop");

    /// <summary>Records one action; the same action again within half a second adds to its count.</summary>
    public static void Note(string what)
    {
        float now = Time.unscaledTime;
        LastAt = now;
        Last = what;
        if (s_stored > 0)
        {
            ref var last = ref s_ring[(s_next + Kept - 1) % Kept];
            if (ReferenceEquals(last.What, what) || last.What == what)
            {
                if (now - last.At <= RepeatSeconds)
                {
                    last.At = now;
                    last.Count++;
                    return;
                }
            }
        }
        s_ring[s_next] = new Entry { At = now, Clock = DateTime.Now, What = what, Count = 1 };
        s_next = (s_next + 1) % Kept;
        if (s_stored < Kept) s_stored++;
    }

    /// <summary>The kept actions, oldest first: "14:02:11.204 skill 3 key-up ×42 (to 14:02:11.900)".</summary>
    public static List<string> Lines()
    {
        var lines = new List<string>(s_stored);
        float now = Time.unscaledTime;
        for (int i = 0; i < s_stored; i++)
        {
            var e = s_ring[(s_next - s_stored + i + Kept) % Kept];
            string line = $"{e.Clock.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {e.What}";
            if (e.Count > 1) line += $" ×{e.Count} (last {(now - e.At).ToString("F1", CultureInfo.InvariantCulture)} s ago)";
            lines.Add(line);
        }
        return lines;
    }

    public static void Clear()
    {
        s_next = s_stored = 0;
        LastAt = float.NegativeInfinity;
        Last = null;
    }
}
