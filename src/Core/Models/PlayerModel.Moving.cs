using UnityEngine;

namespace EpochAssist.Core;

internal static partial class PlayerModel
{
    // Movement from the player's ground position over the last 0.1 s (R20; the game's moving flag stays false while
    // walking by click, GF-78). Sampled each frame something asks.
    private const float MoveWindowSeconds = 0.1f, MoveThreshold = 0.05f;
    private const int MoveSamples = 32;
    private static readonly Vector2[] s_movePositions = new Vector2[MoveSamples];
    private static readonly float[] s_moveTimes = new float[MoveSamples];
    private static int s_moveNext, s_moveStored, s_moveFrame = -1;

    /// <summary>Takes this frame's position sample (the Macros feature calls it every frame it runs).</summary>
    public static void SampleMovement()
    {
        int frame = Time.frameCount;
        if (frame == s_moveFrame) return;
        s_moveFrame = frame;
        if (!Exists || Player.Transform is not Transform transform)
        {
            s_moveStored = 0;
            return;
        }
        float now = Clock.Now;
        // A gap in sampling starts again, so an old position isn't read as movement.
        int last = (s_moveNext - 1 + MoveSamples) % MoveSamples;
        if (s_moveStored > 0 && now - s_moveTimes[last] > 0.5f) s_moveStored = 0;
        Vector3 p = transform.position;
        s_movePositions[s_moveNext] = new Vector2(p.x, p.z);
        s_moveTimes[s_moveNext] = now;
        s_moveNext = (s_moveNext + 1) % MoveSamples;
        if (s_moveStored < MoveSamples) s_moveStored++;
    }

    /// <summary>
    /// Whether the player's ground position changed by more than 0.05 m over the last 0.1 s; null without a player or
    /// before 0.1 s of samples.
    /// </summary>
    public static bool? Moving
    {
        get
        {
            SampleMovement();
            if (s_moveStored < 2) return null;
            int newest = (s_moveNext - 1 + MoveSamples) % MoveSamples;
            float now = s_moveTimes[newest];
            // The newest sample at least the window old.
            for (int n = 1; n < s_moveStored; n++)
            {
                int i = (newest - n + MoveSamples) % MoveSamples;
                if (now - s_moveTimes[i] < MoveWindowSeconds) continue;
                return Vector2.Distance(s_movePositions[newest], s_movePositions[i]) > MoveThreshold;
            }
            return null;
        }
    }

    private static void ResetMovement()
    {
        s_moveStored = 0;
        s_moveFrame = -1;
    }
}
