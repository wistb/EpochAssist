using System.Globalization;
using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Ui;

/// <summary>
/// The Sessions tab's figures: the current session's stats (time, XP and gold with their rates per hour, drops by
/// rarity in the rarity colours, deaths, echoes, time by zone kind), the drops the loot filter showed, and the recent
/// past sessions, read through Sessions only. Characters show as mode and slot.
/// </summary>
internal static class SessionsPage
{
    public const string RecentBlock = "sessions.recent";
    public const string FilteredBlock = "sessions.filtered";
    private const int FilteredCount = 15;

    private const int RecentCount = 25;
    private const float RefreshSeconds = 0.5f;
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    // The current session's totals, taken at most twice a second.
    private static SessionTotals? s_totals;
    private static float s_totalsAt = float.MinValue;
    // The filtered drops line, made again with the totals.
    private static string? s_filtered;
    private static float s_filteredAt = float.MinValue;

    public static void Register()
    {
        MenuBlocks.Register(RecentBlock, (rect, _) => DrawLines(rect, RecentLines()), _ => RecentLines(), (_, _) => Height(RecentLines()));
        MenuBlocks.Register(FilteredBlock, (rect, _) => DrawLines(rect, FilteredLines(colour: true)), _ => FilteredLines(colour: false), (_, _) => Height(FilteredLines(colour: false)));
    }

    public static void Reset()
    {
        s_totals = null;
        s_totalsAt = float.MinValue;
        s_filtered = null;
        s_filteredAt = float.MinValue;
    }

    /// <summary>The rows under "This session": the stats while a session runs, a hint when none does.</summary>
    public static IEnumerable<MenuRow> CurrentRows()
    {
        Func<bool> running = () => Sessions.Current != null;
        yield return new HintRow(() => SessionSettings.History.Value ? "No session yet. One starts when a character enters a zone." : "Session history is off.")
        {
            VisibleWhen = () => !running(),
        };
        yield return Stat("Time", t => $"{Sessions.FormatDuration(t.Duration)} ({Sessions.Current?.Header.CharacterLabel ?? "unknown"})", running);
        yield return Stat("XP", t => $"{Whole(t.Xp)}{PerHour(t.Xp, t)}", running);
        yield return Stat("Gold", t => $"+{Whole(t.GoldGained)}{PerHour(t.GoldGained, t)}{(t.GoldSpent > 0 ? $", spent {Whole(t.GoldSpent)}" : string.Empty)}", running);
        yield return Stat("Drops", Drops, running);
        yield return Stat("Filtered drops", FilteredDrops, running);
        yield return Stat("Deaths", t => Whole(t.Deaths), running);
        yield return Stat("Echoes", t => $"{Whole(t.Echoes)} completed, {Whole(t.Picks)} picked", running);
        yield return Stat("Zones", Zones, running);
    }

    private static LiveText Stat(string label, Func<SessionTotals, string> text, Func<bool> visible) =>
        new(label, () => Totals() is { } t ? (text(t), null) : (string.Empty, null)) { VisibleWhen = visible };

    private static SessionTotals? Totals()
    {
        if (Sessions.Current is not { } current) return null;
        if (s_totals == null || Time.unscaledTime - s_totalsAt >= RefreshSeconds)
        {
            s_totals = current.Totals;
            s_totalsAt = Time.unscaledTime;
        }
        return s_totals;
    }

    // "70: 20 Normal, 21 Magic, …", each rarity in its colour role.
    private static string Drops(SessionTotals t)
    {
        if (t.DropCount == 0) return "0";
        var parts = SessionTotals.DropRarities.Where(r => t.DropsOf(r) > 0)
            .Select(r => Widgets.Colored($"{t.DropsOf(r)} {Capital(r)}", ColourRoles.ItemNamed(r))).ToList();
        if (t.DropsOther > 0) parts.Add($"{t.DropsOther} other");
        return $"{Whole(t.DropCount)}: {string.Join(", ", parts)}";
    }

    // The drops the loot filter showed, by rarity in their colours, counted from the session's drop records.
    private static string FilteredDrops(SessionTotals t)
    {
        if (s_filtered != null && s_filteredAt == s_totalsAt) return s_filtered;
        s_filteredAt = s_totalsAt;
        return s_filtered = CountFiltered(t);
    }

    private static string CountFiltered(SessionTotals t)
    {
        if (t.DropsShown == 0) return "0";
        var shown = Sessions.Current?.Records.Where(r => r.Kind == "drop" && r.Flag("shown") == true)
            .GroupBy(r => (r.Text("rarityName") ?? "normal").ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()) ?? new Dictionary<string, int>();
        var parts = SessionTotals.DropRarities.Where(shown.ContainsKey)
            .Select(r => Widgets.Colored($"{shown[r]} {Capital(r)}", ColourRoles.ItemNamed(r))).ToList();
        return parts.Count > 0 ? $"{Whole(t.DropsShown)}: {string.Join(", ", parts)}" : Whole(t.DropsShown);
    }

    private static string Zones(SessionTotals t)
    {
        var parts = SessionTotals.ZoneKinds.Where(k => t.ZoneSeconds.GetValueOrDefault(k) >= 1d)
            .Select(k => $"{k} {Sessions.FormatDuration(t.ZoneSeconds[k])}").ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : "none yet";
    }

    // " (12,400 per hour)" once a minute has passed.
    private static string PerHour(long amount, SessionTotals t) =>
        t.Duration >= 60d && amount > 0 ? $" ({Whole((long)(amount * 3600d / t.Duration))} per hour)" : string.Empty;

    private static string Whole(long value) => value.ToString("N0", s_inv);

    private static string Capital(string word) => word.Length > 0 ? char.ToUpperInvariant(word[0]) + word.Substring(1) : word;

    // ---- Recent sessions ----

    private static List<string> RecentLines()
    {
        var current = Sessions.Current?.Header.Id;
        var past = Sessions.List().Where(e => e.Id != current && e.Header?.Test != true).Take(RecentCount).ToList();
        if (past.Count == 0) return new List<string> { "No past sessions yet." };
        return past.Select(e =>
        {
            string start = e.Start is { } s ? s.LocalDateTime.ToString("MMM d, HH:mm", s_inv) : "?";
            string totals = e.Totals is { } t
                ? $"{Whole(t.Xp)} XP, +{Whole(t.GoldGained)} gold, {Whole(t.DropCount)} drops ({Whole(t.DropsShown)} shown), {Whole(t.Deaths)} deaths"
                : "totals when it ends";
            string end = e.EndKind == "normal" ? string.Empty : $", {(e.EndKind == "unexpected" ? "ended unexpectedly" : e.EndKind)}";
            return $"{start}  ·  {e.Character}  ·  {Sessions.FormatDuration(e.Duration)}  ·  {totals}{end}";
        }).ToList();
    }

    // ---- Drops the loot filter showed, newest first ----

    private static List<string> FilteredLines(bool colour)
    {
        if (Sessions.Current is not { } session) return new List<string> { "No session yet." };
        var drops = session.Records.Where(r => r.Kind == "drop").ToList();
        var shown = drops.Where(r => r.Flag("shown") == true).Reverse().Take(FilteredCount).ToList();
        if (shown.Count == 0)
            return new List<string> { drops.Any(r => r.Flag("shown") != null) ? "Nothing your loot filter shows has dropped yet." : "No drops read with a loot filter yet (none dropped, or no filter active)." };
        return shown.Select(r =>
        {
            string name = r.Text("name") ?? r.Text("base") ?? "unknown item";
            string rarity = r.Text("rarityName") ?? "Normal";
            string text = colour && Widgets.Themed ? Widgets.Colored(name, ColourRoles.ItemNamed(rarity)) : name;
            string base_ = r.Text("base") is { } b && b != name ? $" ({b})" : string.Empty;
            return $"{Sessions.FormatDuration(r.T)}  ·  {text}{base_}  ·  {rarity}";
        }).ToList();
    }

    private static float Height(List<string> lines) => lines.Count * RowLayout.ControlHeight + RowLayout.RowGap;

    private static void DrawLines(Rect rect, List<string> lines)
    {
        if (Event.current.type != EventType.Repaint) return;
        GUIStyle label = Widgets.Style(() => Theme.Label, GUI.skin.label);
        float y = rect.y;
        foreach (string line in lines)
        {
            Widgets.Paint(new Rect(rect.x, y, rect.width, RowLayout.ControlHeight), line, label);
            y += RowLayout.ControlHeight;
        }
    }
}
