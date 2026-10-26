using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>A fixed colour-blind palette: all fourteen roles in ColourRoles.Names order, and the vision type the role
/// check simulates for it.</summary>
internal sealed record Palette(string Name, string Simulation, Color[] Roles);

/// <summary>
/// The Colour vision setting's palettes (contracts/colour-roles.md). Built from the Okabe–Ito colour-blind-safe set
/// (orange, sky blue, bluish green, yellow, blue, vermilion, reddish purple), lightened in steps where a group needs
/// more colours than the set keeps apart for that vision type, all light enough to read on a dark scene. Tuned until
/// every group is at least ΔE00 10 apart after simulating its vision type (dev `theme check`); the smallest gaps are
/// deuteranopia 15.9, protanopia 16.6 and tritanopia 13.7. They replace every role whatever the theme; the game's own
/// colours don't change.
/// </summary>
internal static class Palettes
{
    private const int White = 0xF0F0F0;

    public static readonly IReadOnlyList<Palette> All = new[]
    {
        new Palette("Deuteranopia", "deuteranopia", Roles(
            items: new[] { White, 0x338EC1, 0xF0E442, 0xCC79A7, 0xDD7E33, 0x009E73, 0x9AD2F2 },
            enemies: new[] { White, 0x338EC1, 0xF0E442, 0xD55E00 },
            danger: new[] { 0xF0E442, 0x56B4E9, 0xD55E00 })),
        new Palette("Protanopia", "protanopia", Roles(
            items: new[] { White, 0x78C3ED, 0xF9F4B3, 0xCC79A7, 0xEBB233, 0x33B18F, 0xD55E00 },
            enemies: new[] { White, 0x4590BA, 0xF0E442, 0xD55E00 },
            danger: new[] { 0xF9F4B3, 0xD694B9, 0xD55E00 })),
        new Palette("Tritanopia", "tritanopia", Roles(
            items: new[] { White, 0xBBE1F6, 0xF3E968, 0xCC79A7, 0xE0AFCA, 0x33B18F, 0xD55E00 },
            enemies: new[] { White, 0x56B4E9, 0xF0C566, 0xD55E00 },
            danger: new[] { 0xF9F4B3, 0x4590BA, 0xD55E00 })),
    };

    /// <summary>The palette for a Colour vision value (1 deuteranopia, 2 protanopia, 3 tritanopia); null for Standard.</summary>
    public static Palette? For(int vision) => vision >= 1 && vision <= All.Count ? All[vision - 1] : null;

    private static Color[] Roles(int[] items, int[] enemies, int[] danger) => items.Concat(enemies).Concat(danger).Select(Hex).ToArray();

    private static Color Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
