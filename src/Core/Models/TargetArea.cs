using UnityEngine;

namespace EpochAssist.Core;

/// <summary>
/// The best spot for an area cast: where a circle of a radius holds the most score (enemy health, weight or count) of
/// the candidates given, on the ground plane. Candidates go into a uniform grid with cells as wide as the radius, so a
/// circle's members come from at most 9 cells. Starts: each occupied cell's centroid and the two heaviest enemies; the
/// best three, apart, are refined by a pattern search that also tries mean-shift steps (to the score-weighted centroid
/// of the members). Ties go to the centre nearest the hero; the spot is kept within range of the hero. Plain floats
/// throughout (Unity's math types call into the game); nothing is allocated once the pools have grown.
/// </summary>
internal static class TargetArea
{
    /// <summary>One search's answer: the spot (height = its members' mean), how many candidates lie within the radius,
    /// and their total score.</summary>
    public readonly record struct Spot(Vector3 Centre, int Members, float Score);

    private const int MaxCells = 4096;
    private static int[] s_cellStart = new int[MaxCells + 1];
    private static int[] s_order = new int[64];
    private static int[] s_cellOf = new int[64];
    // The candidates' ground places and scores in cell order, so a circle's look reads them in sequence.
    private static float[] s_sx = new float[64], s_sz = new float[64], s_ss = new float[64];
    private static float s_minX, s_minZ, s_cell;
    private static int s_width, s_height;

    // Starts kept for the refining search, and the search's smallest step as a share of the radius.
    private const int Starts = 3;
    private const float FinestStep = 1f / 16f;
    // Circle looks one refine may take (a dense pack's look reads every member).
    private const int MaxLooks = 40;
    private static readonly float[] s_startX = new float[Starts], s_startZ = new float[Starts], s_startScores = new float[Starts];

    /// <summary>
    /// The best spot among count candidates (x, z on the ground, y height, score each). With one candidate the spot is
    /// that candidate. The spot is pulled within range of the hero (heroX, heroZ).
    /// </summary>
    public static Spot Best(float[] x, float[] y, float[] z, float[] score, int count, float radius, float range, float heroX, float heroZ)
    {
        if (count == 0) return default;
        if (count == 1) return new Spot(new Vector3(x[0], y[0], z[0]), 1, score[0]);
        Bucket(x, z, count, radius);
        SortScores(score, count);
        // Coarse: each occupied cell's centroid, one mean-shift step; the best few (apart) are refined.
        int kept = 0;
        int cells = s_width * s_height;
        for (int cell = 0; cell < cells; cell++)
        {
            int first = s_cellStart[cell], end = s_cellStart[cell + 1];
            if (first == end) continue;
            float cx = 0f, cz = 0f;
            for (int k = first; k < end; k++)
            {
                cx += x[s_order[k]];
                cz += z[s_order[k]];
            }
            cx /= end - first;
            cz /= end - first;
            kept = Keep(cx, cz, Sum(x, z, score, radius, cx, cz, out _, out _), kept, radius);
        }
        // The two heaviest single enemies start too (a boss's spot in health or weight is often not a cell's centroid).
        int heaviest = -1, second = -1;
        for (int i = 0; i < count; i++)
        {
            if (heaviest < 0 || score[i] > score[heaviest])
            {
                second = heaviest;
                heaviest = i;
            }
            else if (second < 0 || score[i] > score[second]) second = i;
        }
        foreach (int i in stackalloc[] { heaviest, second })
        {
            if (i < 0) continue;
            kept = Keep(x[i], z[i], Sum(x, z, score, radius, x[i], z[i], out _, out _), kept, radius);
        }
        float bestScore = float.NegativeInfinity, bestHero = float.PositiveInfinity, bestX = 0f, bestZ = 0f;
        for (int i = 0; i < kept; i++)
        {
            float cx = s_startX[i], cz = s_startZ[i];
            float s = Refine(x, z, score, radius, ref cx, ref cz, s_startScores[i]);
            float d = (cx - heroX) * (cx - heroX) + (cz - heroZ) * (cz - heroZ);
            if (s > bestScore + 1e-4f || (MathF.Abs(s - bestScore) <= 1e-4f && d < bestHero))
            {
                bestScore = s;
                bestHero = d;
                bestX = cx;
                bestZ = cz;
            }
        }
        Clamp(ref bestX, ref bestZ, heroX, heroZ, range);
        return Measure(x, y, z, score, radius, bestX, bestZ);
    }

    /// <summary>
    /// A kept spot moved with its pack: from its old centre (fromX, fromZ), up to two mean-shift steps and the refining
    /// search over the candidates, kept within range of the hero.
    /// </summary>
    public static Spot Recentre(float[] x, float[] y, float[] z, float[] score, int count, float radius, float range, float heroX, float heroZ, float fromX, float fromZ)
    {
        if (count == 0) return default;
        Bucket(x, z, count, radius);
        SortScores(score, count);
        float cx = fromX, cz = fromZ;
        float s = Climb(x, z, score, radius, ref cx, ref cz);
        Refine(x, z, score, radius, ref cx, ref cz, s);
        Clamp(ref cx, ref cz, heroX, heroZ, range);
        return Measure(x, y, z, score, radius, cx, cz);
    }

    /// <summary>A spot moved onto its members' current places: one mean-shift step (the send's re-centring).</summary>
    public static Spot Shift(float[] x, float[] y, float[] z, float[] score, int count, float radius, float range, float heroX, float heroZ, float fromX, float fromZ)
    {
        if (count == 0) return default;
        Bucket(x, z, count, radius);
        SortScores(score, count);
        float s = Sum(x, z, score, radius, fromX, fromZ, out float mx, out float mz);
        float cx = fromX, cz = fromZ;
        if (Sum(x, z, score, radius, mx, mz, out _, out _) >= s)
        {
            cx = mx;
            cz = mz;
        }
        Clamp(ref cx, ref cz, heroX, heroZ, range);
        return Measure(x, y, z, score, radius, cx, cz);
    }

    // Keeps the best few starts, at least half a radius apart (a better one nearby replaces the worse).
    private static int Keep(float cx, float cz, float s, int kept, float radius)
    {
        float apart = radius * radius / 4f;
        for (int i = 0; i < kept; i++)
        {
            float dx = s_startX[i] - cx, dz = s_startZ[i] - cz;
            if (dx * dx + dz * dz >= apart) continue;
            if (s > s_startScores[i]) Put(i, cx, cz, s);
            return kept;
        }
        if (kept < Starts)
        {
            Put(kept, cx, cz, s);
            return kept + 1;
        }
        int worst = 0;
        for (int i = 1; i < kept; i++)
        {
            if (s_startScores[i] < s_startScores[worst]) worst = i;
        }
        if (s > s_startScores[worst]) Put(worst, cx, cz, s);
        return kept;
    }

    private static void Put(int i, float cx, float cz, float s)
    {
        s_startX[i] = cx;
        s_startZ[i] = cz;
        s_startScores[i] = s;
    }

    private static readonly int[] s_moveX = { 1, -1, 0, 0, 1, 1, -1, -1 }, s_moveZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

    // A pattern search from a start: first its members' weighted centroid (a mean-shift step), then the 4 neighbours a
    // step away along the axes, then the 4 diagonal ones; move to the first that is better, else halve the step, down to
    // a sixteenth of the radius. The score at the end comes back; the centre moves with it.
    private static float Refine(float[] x, float[] z, float[] score, float radius, ref float cx, ref float cz, float s)
    {
        Sum(x, z, score, radius, cx, cz, out float mx, out float mz);
        float step = radius / 2f;
        int looks = 0;
        for (int guard = 0; guard < 40 && step >= radius * FinestStep && looks < MaxLooks; guard++)
        {
            looks++;
            float t = Sum(x, z, score, radius, mx, mz, out float nx, out float nz);
            if (t > s + 1e-4f)
            {
                (cx, cz, s, mx, mz) = (mx, mz, t, nx, nz);
                continue;
            }
            bool moved = false;
            for (int m = 0; m < 8 && !moved; m++)
            {
                looks++;
                float ax = cx + s_moveX[m] * step, az = cz + s_moveZ[m] * step;
                t = Sum(x, z, score, radius, ax, az, out nx, out nz);
                if (t <= s + 1e-4f) continue;
                (cx, cz, s, mx, mz) = (ax, az, t, nx, nz);
                moved = true;
            }
            if (!moved) step /= 2f;
        }
        return s;
    }

    // The score at a centre, after up to two mean-shift steps that don't lower it; the centre moves with them.
    private static float Climb(float[] x, float[] z, float[] score, float radius, ref float cx, ref float cz)
    {
        float s = Sum(x, z, score, radius, cx, cz, out float mx, out float mz);
        for (int step = 0; step < 2; step++)
        {
            if ((mx - cx) * (mx - cx) + (mz - cz) * (mz - cz) < 1e-4f) break;
            float next = Sum(x, z, score, radius, mx, mz, out float nx, out float nz);
            if (next < s - 1e-4f) break;
            cx = mx;
            cz = mz;
            s = next;
            mx = nx;
            mz = nz;
        }
        return s;
    }

    private static void Clamp(ref float cx, ref float cz, float heroX, float heroZ, float range)
    {
        float dx = cx - heroX, dz = cz - heroZ, d = MathF.Sqrt(dx * dx + dz * dz);
        if (d <= range || d < 1e-6f) return;
        cx = heroX + dx / d * range;
        cz = heroZ + dz / d * range;
    }

    // The grid cells a circle touches.
    private static void Cells(float cx, float cz, float radius, out int x0, out int x1, out int z0, out int z1)
    {
        x0 = Math.Max(0, (int)MathF.Floor((cx - radius - s_minX) / s_cell));
        x1 = Math.Min(s_width - 1, (int)MathF.Floor((cx + radius - s_minX) / s_cell));
        z0 = Math.Max(0, (int)MathF.Floor((cz - radius - s_minZ) / s_cell));
        z1 = Math.Min(s_height - 1, (int)MathF.Floor((cz + radius - s_minZ) / s_cell));
    }

    // The candidates within radius of a centre: their total score and score-weighted centroid (by position alone when
    // nothing there weighs, so members of unknown health still pull; the centre itself with none).
    private static float Sum(float[] x, float[] z, float[] score, float radius, float cx, float cz, out float mx, out float mz)
    {
        Cells(cx, cz, radius, out int x0, out int x1, out int z0, out int z1);
        float r2 = radius * radius, total = 0f, wx = 0f, wz = 0f, px = 0f, pz = 0f;
        int members = 0;
        for (int gz = z0; gz <= z1; gz++)
        {
            int row = gz * s_width;
            for (int gx = x0; gx <= x1; gx++)
            {
                int cell = row + gx;
                for (int k = s_cellStart[cell], end = s_cellStart[cell + 1]; k < end; k++)
                {
                    float kx = s_sx[k], kz = s_sz[k], dx = kx - cx, dz = kz - cz;
                    if (dx * dx + dz * dz > r2) continue;
                    float w = s_ss[k];
                    total += w;
                    wx += w * kx;
                    wz += w * kz;
                    px += kx;
                    pz += kz;
                    members++;
                }
            }
        }
        if (total > 1e-6f)
        {
            mx = wx / total;
            mz = wz / total;
        }
        else if (members > 0)
        {
            mx = px / members;
            mz = pz / members;
        }
        else
        {
            mx = cx;
            mz = cz;
        }
        return total;
    }

    private static Spot Measure(float[] x, float[] y, float[] z, float[] score, float radius, float cx, float cz)
    {
        Cells(cx, cz, radius, out int x0, out int x1, out int z0, out int z1);
        float r2 = radius * radius, total = 0f, height = 0f;
        int members = 0;
        for (int gz = z0; gz <= z1; gz++)
        {
            for (int gx = x0; gx <= x1; gx++)
            {
                int cell = gz * s_width + gx;
                for (int k = s_cellStart[cell]; k < s_cellStart[cell + 1]; k++)
                {
                    int i = s_order[k];
                    float dx = x[i] - cx, dz = z[i] - cz;
                    if (dx * dx + dz * dz > r2) continue;
                    total += score[i];
                    height += y[i];
                    members++;
                }
            }
        }
        return new Spot(new Vector3(cx, members > 0 ? height / members : 0f, cz), members, total);
    }

    // Counting sort of the candidates into grid cells; a spread too wide for the cell table gets wider cells (still
    // exact, only more candidates per look).
    private static void Bucket(float[] x, float[] z, int count, float radius)
    {
        if (s_order.Length < count)
        {
            int size = s_order.Length;
            while (size < count) size *= 2;
            s_order = new int[size];
            s_cellOf = new int[size];
            s_sx = new float[size];
            s_sz = new float[size];
            s_ss = new float[size];
        }
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            minX = MathF.Min(minX, x[i]);
            maxX = MathF.Max(maxX, x[i]);
            minZ = MathF.Min(minZ, z[i]);
            maxZ = MathF.Max(maxZ, z[i]);
        }
        float cell = MathF.Max(radius, 0.5f);
        while (((int)((maxX - minX) / cell) + 1) * ((int)((maxZ - minZ) / cell) + 1) > MaxCells) cell *= 2f;
        s_minX = minX;
        s_minZ = minZ;
        s_cell = cell;
        s_width = (int)((maxX - minX) / cell) + 1;
        s_height = (int)((maxZ - minZ) / cell) + 1;
        int cells = s_width * s_height;
        Array.Clear(s_cellStart, 0, cells + 1);
        for (int i = 0; i < count; i++)
        {
            int c = Math.Min(s_height - 1, (int)((z[i] - minZ) / cell)) * s_width + Math.Min(s_width - 1, (int)((x[i] - minX) / cell));
            s_cellOf[i] = c;
            s_cellStart[c + 1]++;
        }
        for (int c = 0; c < cells; c++) s_cellStart[c + 1] += s_cellStart[c];
        // Fill each cell's slots from its start; the starts are moved back by one cell afterwards.
        for (int i = 0; i < count; i++) s_order[s_cellStart[s_cellOf[i]]++] = i;
        for (int c = cells; c > 0; c--) s_cellStart[c] = s_cellStart[c - 1];
        s_cellStart[0] = 0;
        for (int k = 0; k < count; k++)
        {
            int i = s_order[k];
            s_sx[k] = x[i];
            s_sz[k] = z[i];
        }
    }

    // The scores in cell order (after Bucket).
    private static void SortScores(float[] score, int count)
    {
        for (int k = 0; k < count; k++) s_ss[k] = score[s_order[k]];
    }

#if DEV
    /// <summary>
    /// Dev bench only: the best score over centres at every candidate and at both intersections of every pair of
    /// candidate circles (O(n³)); any best circle can be moved until two members sit on its edge, so this is the exact
    /// best for count (and close to it for weighted scores).
    /// </summary>
    public static float ExactBest(float[] x, float[] z, float[] score, int count, float radius)
    {
        float best = 0f, r2 = radius * radius;
        float Score(float cx, float cz)
        {
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                float dx = x[i] - cx, dz = z[i] - cz;
                if (dx * dx + dz * dz <= r2 + 1e-3f) total += score[i];
            }
            return total;
        }
        for (int i = 0; i < count; i++)
        {
            best = MathF.Max(best, Score(x[i], z[i]));
            for (int j = i + 1; j < count; j++)
            {
                float dx = x[j] - x[i], dz = z[j] - z[i], d2 = dx * dx + dz * dz;
                if (d2 > 4f * r2 || d2 < 1e-8f) continue;
                float d = MathF.Sqrt(d2), h = MathF.Sqrt(MathF.Max(0f, r2 - d2 / 4f));
                float mx = x[i] + dx / 2f, mz = z[i] + dz / 2f, ox = -dz / d * h, oz = dx / d * h;
                best = MathF.Max(best, MathF.Max(Score(mx + ox, mz + oz), Score(mx - ox, mz - oz)));
            }
        }
        return best;
    }
#endif
}
