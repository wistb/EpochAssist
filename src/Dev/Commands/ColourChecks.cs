using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Dev;

// The colour role distance check behind `theme check`: within each group (item rarity, enemy rarity, danger, zone kind) the
// smallest CIEDE2000 difference between two roles, after simulating a colour vision type with the Machado, Oliveira
// and Fernandes (2009) matrices at full severity, applied in linear RGB.
internal static class ColourChecks
{
    public const double MinDistance = 10d;

    private static readonly (string Name, int First, int Count)[] s_groups =
    {
        ("item", ColourRoles.ItemFirst, 7), ("enemy", ColourRoles.EnemyFirst, 4), ("danger", ColourRoles.DangerFirst, 3),
        ("zone", ColourRoles.ZoneFirst, 4),
    };

    private static readonly Dictionary<string, double[,]> s_simulations = new()
    {
        ["protanopia"] = new[,] { { 0.152286, 1.052583, -0.204868 }, { 0.114503, 0.786281, 0.099216 }, { -0.003882, -0.048116, 1.051998 } },
        ["deuteranopia"] = new[,] { { 0.367322, 0.860646, -0.227968 }, { 0.280085, 0.672501, 0.047413 }, { -0.011820, 0.042940, 0.968881 } },
        ["tritanopia"] = new[,] { { 1.255528, -0.076749, -0.178779 }, { -0.078411, 0.930809, 0.147602 }, { 0.004733, 0.691367, 0.303900 } },
    };

    /// <summary>"roles Carbonfox: item ΔE min 19.4 (magic/exalted), enemy 27.8, danger 25.2 pass", or fail with the pairs under 10.</summary>
    public static string RoleLine(string name, Color[] roles, string? simulation)
    {
        var parts = new List<string>();
        var failures = new List<string>();
        foreach (var (group, first, count) in s_groups)
        {
            var labs = Enumerable.Range(first, count).Select(i => Lab(Simulate(roles[i], simulation))).ToArray();
            double min = double.MaxValue;
            string pair = string.Empty;
            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    double d = DeltaE00(labs[a], labs[b]);
                    string names = $"{Short(first + a)}/{Short(first + b)}";
                    if (d < min) (min, pair) = (d, names);
                    if (d < MinDistance) failures.Add($"{group} {names} {F1(d)}");
                }
            }
            parts.Add(group == "item" ? $"item ΔE min {F1(min)} ({pair})" : $"{group} {F1(min)} ({pair})");
        }
        return $"roles {name}{(simulation != null ? $" ({simulation})" : string.Empty)}: {string.Join(", ", parts)} "
            + (failures.Count == 0 ? "pass" : "fail (" + string.Join(", ", failures) + ")");
    }

    private static string F1(double value) => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static string Short(int role) => ColourRoles.Names[role].Split('.').Last();

    private static double Linear(double x) => x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);

    private static double[] Simulate(Color c, string? simulation)
    {
        var rgb = new[] { Linear(c.r), Linear(c.g), Linear(c.b) };
        if (simulation == null || !s_simulations.TryGetValue(simulation, out var m)) return rgb;
        var result = new double[3];
        for (int i = 0; i < 3; i++) result[i] = Math.Clamp(m[i, 0] * rgb[0] + m[i, 1] * rgb[1] + m[i, 2] * rgb[2], 0d, 1d);
        return result;
    }

    // Linear sRGB → XYZ (D65) → CIELAB.
    private static (double L, double A, double B) Lab(double[] rgb)
    {
        double x = 0.4124564 * rgb[0] + 0.3575761 * rgb[1] + 0.1804375 * rgb[2];
        double y = 0.2126729 * rgb[0] + 0.7151522 * rgb[1] + 0.0721750 * rgb[2];
        double z = 0.0193339 * rgb[0] + 0.1191920 * rgb[1] + 0.9503041 * rgb[2];
        static double F(double t) => t > 216d / 24389d ? Math.Cbrt(t) : (24389d / 27d * t + 16d) / 116d;
        double fx = F(x / 0.95047), fy = F(y), fz = F(z / 1.08883);
        return (116d * fy - 16d, 500d * (fx - fy), 200d * (fy - fz));
    }

    // CIEDE2000 (Sharma, Wu and Dalal 2005).
    private static double DeltaE00((double L, double A, double B) p, (double L, double A, double B) q)
    {
        static double Rad(double deg) => deg * Math.PI / 180d;
        double c1 = Math.Sqrt(p.A * p.A + p.B * p.B), c2 = Math.Sqrt(q.A * q.A + q.B * q.B), cBar = (c1 + c2) / 2d;
        double g = 0.5 * (1d - Math.Sqrt(Math.Pow(cBar, 7) / (Math.Pow(cBar, 7) + Math.Pow(25d, 7))));
        double a1 = (1d + g) * p.A, a2 = (1d + g) * q.A;
        double c1p = Math.Sqrt(a1 * a1 + p.B * p.B), c2p = Math.Sqrt(a2 * a2 + q.B * q.B);
        double h1p = c1p == 0d ? 0d : (Math.Atan2(p.B, a1) * 180d / Math.PI + 360d) % 360d;
        double h2p = c2p == 0d ? 0d : (Math.Atan2(q.B, a2) * 180d / Math.PI + 360d) % 360d;
        double dLp = q.L - p.L, dCp = c2p - c1p, dhp = 0d;
        if (c1p * c2p != 0d)
        {
            dhp = h2p - h1p;
            if (dhp > 180d) dhp -= 360d;
            else if (dhp < -180d) dhp += 360d;
        }
        double dHp = 2d * Math.Sqrt(c1p * c2p) * Math.Sin(Rad(dhp / 2d));
        double lBar = (p.L + q.L) / 2d, cBarP = (c1p + c2p) / 2d, hBar;
        if (c1p * c2p == 0d) hBar = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180d) hBar = (h1p + h2p) / 2d;
        else hBar = h1p + h2p < 360d ? (h1p + h2p + 360d) / 2d : (h1p + h2p - 360d) / 2d;
        double t = 1d - 0.17 * Math.Cos(Rad(hBar - 30d)) + 0.24 * Math.Cos(Rad(2d * hBar)) + 0.32 * Math.Cos(Rad(3d * hBar + 6d)) - 0.20 * Math.Cos(Rad(4d * hBar - 63d));
        double dTheta = 30d * Math.Exp(-Math.Pow((hBar - 275d) / 25d, 2));
        double rc = 2d * Math.Sqrt(Math.Pow(cBarP, 7) / (Math.Pow(cBarP, 7) + Math.Pow(25d, 7)));
        double sl = 1d + 0.015 * Math.Pow(lBar - 50d, 2) / Math.Sqrt(20d + Math.Pow(lBar - 50d, 2));
        double sc = 1d + 0.045 * cBarP, sh = 1d + 0.015 * cBarP * t;
        double rt = -Math.Sin(Rad(2d * dTheta)) * rc;
        return Math.Sqrt(Math.Pow(dLp / sl, 2) + Math.Pow(dCp / sc, 2) + Math.Pow(dHp / sh, 2) + rt * (dCp / sc) * (dHp / sh));
    }
}
