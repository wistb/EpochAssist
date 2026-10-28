using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// Diagnostics, General › Diagnostics in the menu: the mod's build and mode with the log level (Overview), each feature's
/// state with its switch (Features), other mods and where they overlap ours (Mods), crash reports (Reports), and a copy of
/// it all as scrubbed text for a bug report.
/// The feature owns the copy and open actions; the pages are the menu's blocks.
/// </summary>
internal sealed class DiagnosticsFeature : Feature
{
    public override string Id => "diagnostics";
    public override string Name => "Diagnostics";

    public override void Start()
    {
        Actions.Register(Id, "diagnostics.copy", "Copy diagnostics", () => DiagnosticsPage.Copy(Diagnostics.Build().ToText(), "Diagnostics copied."));
        Actions.Register(Id, "diagnostics.open", "Open diagnostics", () => Ui.Menu.Open(MenuRoutes.Overview));
    }

    public override void Shutdown() => DiagnosticsPage.Reset();
}

internal static class DiagnosticsPage
{
    private const float RebuildSeconds = 0.5f;

    private static DiagnosticsSnapshot? s_snapshot;
    private static float s_builtAt = float.MinValue;
    private static OverlapReport? s_overlap;
    private static float s_overlapAt = float.MinValue;
    private static bool s_clearing;
    private static Action? s_pending;

    public static void Reset()
    {
        s_snapshot = null;
        s_builtAt = float.MinValue;
        s_overlap = null;
        s_overlapAt = float.MinValue;
        s_clearing = false;
        s_pending = null;
    }

    /// <summary>Puts text on the clipboard and says how it went; a failure points at the page, where the text is shown.</summary>
    public static void Copy(string text, string done)
    {
        try
        {
            GUIUtility.systemCopyBuffer = text;
            Toasts.Show("diagnostics", done, ToastKind.Info);
        }
        catch (Exception e)
        {
            Log.WarnThrottled("diagnostics:copy", $"Diagnostics copy failed: {e.Message}");
            Toasts.Show("diagnostics", "Copy failed; the text is on the page.", ToastKind.Warning);
        }
    }

    public static void Register()
    {
        MenuBlocks.Register("diagnostics.overview", (rect, _) => Overview(rect), _ => OverviewLines(Snapshot()).Select(Plain), (_, width) => OverviewHeight(width));
        MenuBlocks.Register("diagnostics.features", (rect, _) => Page(rect, Features), _ => Snapshot().Features.Select(f => $"feature {f.Id} {RowText.Quote(f.StateText)}"));
        MenuBlocks.Register("diagnostics.reports", (rect, _) => Page(rect, ReportsCard), _ => Reports.List().Select(r => $"report {RowText.Quote(r.File)}"));
        MenuBlocks.Register("diagnostics.mods", (rect, _) => Page(rect, ModsCard), _ => ModsLines(Overlap()).Where(l => !l.Note).Select(l => $"{l.Kind} {RowText.Quote(l.Text)}"));
        // The Mods page's switches are drawn in its block, not as setting rows, so search is told about them here.
        MenuSearch.AddSource("diagnostics.mods", () => 1, () => new[] { Config.ModConflictNotice, Config.ModConflictWarnings }
            .Select(t => new SearchEntry("Config › Diagnostics › Mods", $"Mod conflicts: {t.Label}. {t.Help}", () =>
            {
                if (MenuRoutes.Resolve(ModsPage) is { } page) Menu.Open(page);
            })).ToList());
    }

    private const string ModsPage = "combat/diagnostics/mods";

    /// <summary>Opens the console's page.</summary>
    public static void ShowConsole() => Menu.Open(MenuRoutes.Console);

    // Rebuilt in a layout pass at most twice a second, so the passes of one frame draw the same thing.
    private static DiagnosticsSnapshot Snapshot()
    {
        if (s_snapshot == null || (Event.current?.type == EventType.Layout && Time.unscaledTime - s_builtAt >= RebuildSeconds))
        {
            s_snapshot = Diagnostics.Build();
            s_builtAt = Time.unscaledTime;
        }
        return s_snapshot;
    }

    // Like the snapshot: rebuilt in a layout pass at most twice a second.
    private static OverlapReport Overlap()
    {
        if (s_overlap == null || (Event.current?.type == EventType.Layout && Time.unscaledTime - s_overlapAt >= RebuildSeconds))
        {
            s_overlap = ModOverlap.Check();
            s_overlapAt = Time.unscaledTime;
        }
        return s_overlap;
    }

    private static string Plain(string line) => RowText.StripTags(line);

    // A full-page block: its rows laid out in a scroll view.
    private static void Page(Rect rect, Action draw)
    {
        if (s_pending != null && Event.current.type == EventType.Layout)
        {
            var pending = s_pending;
            s_pending = null;
            pending();
        }
        GUILayout.BeginArea(rect);
        Popups.PushOrigin(rect.position);
        Menu.BeginScroll();
        draw();
        Menu.EndScroll();
        Popups.PopOrigin();
        GUILayout.EndArea();
    }

    // ---- Overview: lines of text among the page's rows ----

    private static List<string> OverviewLines(DiagnosticsSnapshot s)
    {
        var lines = new List<string>
        {
            $"Version {BuildInfo.Version}, build {Runtime.BuildId}{(Runtime.Hosted ? ", dev build" : string.Empty)}.",
            $"Tested game version: {s.TestedGameVersion}.",
            s.Mode == "not in game" ? $"Not in game; scene '{s.Scene}'." : $"Playing {s.Mode} in '{s.Scene}' ({s.SceneKind}).",
            $"Frame time for the whole mod: {s.ModFrameText}.",
            $"Changes to the game: {s.ChangesText}.",
        };
        lines.AddRange(s.Edits.Select(e => Colored(e, Theme.Dim)));
        lines.Add($"Profile: {s.Profiles}.");
        foreach (string problem in s.SettingsFailures.Concat(s.ProblemFiles).Concat(MenuTree.Problems.Select(p => "Menu: " + p))) lines.Add(Colored(problem, Theme.Pause));
        lines.Add(Colored("Copy diagnostics puts all of these pages on the clipboard for a bug report, without personal data.", Theme.Dim));
        return lines;
    }

    private static float OverviewHeight(float width) => OverviewLines(Snapshot()).Sum(line => Controls.HintHeight(line, width)) + 4f;

    private static void Overview(Rect rect)
    {
        if (Event.current.type != EventType.Repaint) return;
        float y = rect.y;
        foreach (string line in OverviewLines(Snapshot()))
        {
            float h = Controls.HintHeight(line, rect.width);
            Widgets.Paint(new Rect(rect.x, y, rect.width, h), line, Style(() => Theme.Hint, GUI.skin.label));
            y += h;
        }
    }

    // ---- Features: one row each, with its switch where it can be turned off ----

    private static void Features()
    {
        foreach (var feature in Snapshot().Features) FeatureRow(feature);
    }

    // Newest first, each with its own copy button. The note and Clear share the top row, Clear at the right in line with
    // the Copy buttons; Clear asks there before deleting.
    private static void ReportsCard()
    {
        var reports = Reports.List();
        TopRow(reports.Count);
        if (reports.Count == 0) Note("No reports.");
        foreach (var report in reports)
        {
            Rect row = GUILayoutUtility.GetRect(10f, RowLayout.ControlHeight + RowLayout.RowGap, GUILayout.ExpandWidth(true));
            float w = Controls.ButtonWidth("Copy");
            if (Event.current.type == EventType.Repaint)
                Widgets.Paint(new Rect(row.x, row.y, row.width - w - 8f, RowLayout.ControlHeight),
                    $"{report.Time}  ·  {report.Feature}{(report.Count > 1 ? $"  ·  {report.Count} times" : string.Empty)}", Style(() => Theme.Label, GUI.skin.label));
            if (Controls.Button(new Rect(row.xMax - w, row.y, w, RowLayout.ControlHeight), "Copy", "report." + report.File)) CopyReport(report.File);
        }
    }

    private static void TopRow(int count)
    {
        Rect row = GUILayoutUtility.GetRect(10f, RowLayout.ControlHeight + RowLayout.RowGap + 4f, GUILayout.ExpandWidth(true));
        var line = new Rect(row.x, row.y, row.width, RowLayout.ControlHeight);
        string text = s_clearing
            ? Colored($"Delete {(count == 1 ? "the report" : $"all {count} reports")}?", Theme.Pause) + " They can't be brought back."
            : "Saved in UserData/EpochAssist/reports. Nothing is sent anywhere.";
        float right = line.xMax;
        if (count > 0 && !s_clearing)
        {
            float clear = Controls.ButtonWidth("Clear");
            right -= clear;
            if (Controls.Button(new Rect(right, line.y, clear, line.height), "Clear", "reports.clear")) s_clearing = true;
        }
        else if (count > 0)
        {
            float keep = Controls.ButtonWidth("Keep"), delete = Controls.ButtonWidth("Delete");
            right -= keep;
            if (Controls.Button(new Rect(right, line.y, keep, line.height), "Keep", "reports.clear.keep")) s_clearing = false;
            right -= delete + 6f;
            // Run in the next layout pass, so this frame's passes still draw the same list.
            if (Controls.Button(new Rect(right, line.y, delete, line.height), "Delete", "reports.clear.confirm", primary: true))
                s_pending = () =>
                {
                    s_clearing = false;
                    if (Reports.Clear(out string reason)) Toasts.Show("diagnostics", "Reports cleared.", ToastKind.Info);
                    else Toasts.Show("diagnostics", $"Reports not cleared: {reason}.", ToastKind.Warning);
                };
        }
        if (Event.current.type != EventType.Repaint) return;
        // Centred on the buttons' row.
        float width = right - line.x - 8f, height = Controls.HintHeight(text, width);
        Widgets.Paint(new Rect(line.x, Mathf.Round(line.center.y - height / 2f), width, height), text, Style(() => Theme.Hint, GUI.skin.label));
    }

    // ---- Mods: loaded mods, shared patch targets and known overlaps; overlaps change nothing ----

    // Loaded mods with a mark each (ours, in conflict, no overlap), the overlaps grouped by the other mod, and the known
    // mods with what they may affect; colour and icons carry what matters, the words stay as the bridge reads them.
    private static void ModsCard()
    {
        var report = Overlap();
        var conflicting = new HashSet<string>(report.OverlappingNames(), StringComparer.OrdinalIgnoreCase);
        foreach (var known in report.Known) conflicting.Add(known.Name);

        ProfilesPage.Heading("Warnings");
        SwitchLine(Config.ModConflictNotice);
        SwitchLine(Config.ModConflictWarnings);

        ProfilesPage.Heading("Loaded mods");
        foreach (var mod in report.Mods)
        {
            bool conflict = !mod.Ours && (conflicting.Contains(mod.Name) || conflicting.Contains(Stem(mod.File)));
            var (icon, colour, mark) = mod.Ours ? (Theme.IconDot, Theme.Good, "this mod")
                : mod.Kind == "failed" ? (Theme.IconWarning, Theme.Error, "failed to load")
                : conflict ? (Theme.IconWarning, Controls.ConflictColor, "conflict")
                : (Theme.IconDash, Theme.Dim, "no overlap");
            string detail = mod.Kind == "dev-loaded" ? $"build {Runtime.BuildId}, loaded by the dev loader"
                : mod.Kind == "failed" ? mod.Error ?? mod.File
                : $"{mod.Version} by {mod.Author} · {mod.File}{(mod.Kind == "plugin" ? ", plugin" : string.Empty)}";
            IconLine(icon, colour, $"{mod.Name}  {Colored(detail, Theme.Dim)}", Colored(mark, Theme.LightText(colour)));
        }
        if (!report.Mods.Any(m => !m.Ours)) Note(Colored("No other mods.", Theme.Dim));
        foreach (string problem in report.Problems) Note(Colored(problem, Theme.Pause));

        ProfilesPage.Heading("Overlaps");
        if (report.Overlaps.Count == 0) Note(Colored("None: no other mod patches a method ours do.", Theme.Dim));
        foreach (var group in report.Overlaps.SelectMany(o => o.Others.Select(x => x.Owner)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IconLine(Theme.IconWarning, Controls.ConflictColor, Colored(group, Theme.LightText(Controls.ConflictColor)), null);
            foreach (var overlap in report.Overlaps.Where(o => o.Others.Any(x => string.Equals(x.Owner, group, StringComparison.OrdinalIgnoreCase))))
            {
                string kinds = string.Join(", ", overlap.Others.Where(x => string.Equals(x.Owner, group, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(x => x.Kind).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
                string feature = FeatureName(overlap.Feature) + (overlap.OursApplied ? string.Empty : " (when needed)");
                Indented($"{Colored(feature, Theme.LightText(Theme.AccentText))}  {Colored(overlap.Method, Theme.Text)}  {Colored(kinds, Theme.Dim)}");
            }
        }

        ProfilesPage.Heading("Known overlaps");
        if (report.Known.Count == 0) Note(Colored("None of the loaded mods is on the known list.", Theme.Dim));
        foreach (var known in report.Known)
        {
            IconLine(Theme.IconWarning, Controls.ConflictColor, Colored(known.Name, Theme.LightText(Controls.ConflictColor)), null);
            string affects = known.Affects.Length > 0 ? $" · may affect {Colored(string.Join(", ", known.Affects.Select(FeatureName)), Theme.LightText(Theme.AccentText))}" : string.Empty;
            Indented($"{Colored(string.Join(", ", known.Kinds), Theme.Dim)}{Colored(affects, Theme.Text)}");
            Indented(Colored(known.Text, Theme.Secondary));
        }
        GUILayout.Space(6f);
        Note(Colored("Overlaps change nothing: every feature keeps running. They point at what to check when something acts twice or looks odd.", Theme.Dim));
    }

    // A setting's switch on its own line in a laid-out block: the label, the switch and its help, as a setting row
    // draws them.
    private static void SwitchLine(Toggle toggle)
    {
        float width = PageView.BlockWidth;
        Rect r = GUILayoutUtility.GetRect(width, RowLayout.Height(width, Controls.SwitchWidth, toggle.Help), GUILayout.ExpandWidth(true));
        var parts = RowLayout.Lay(r, Controls.SwitchWidth, toggle.Help);
        if (Event.current.type == EventType.Repaint) Widgets.Paint(parts.Label, toggle.Label, Style(() => Theme.Label, GUI.skin.label));
        if (Controls.Switch(parts.Control, toggle.Value, "mods." + toggle.Id, toggle.Help) != toggle.Value) SettingRows.Flip(toggle, "mods page switch");
        RowLayout.Hint(parts.Hint, toggle.Help);
    }

    private static string Stem(string file) => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;

    // A feature's name as the menu shows it ("Macros"), else its id.
    private static string FeatureName(string id) => FeatureHost.Find(id)?.Name ?? id;

    // A line with a tinted icon before it and, when given, a word at the right.
    private static void IconLine(Texture2D icon, Color colour, string text, string? right)
    {
        float width = PageView.BlockWidth;
        GUIStyle style = Style(() => Theme.Label, GUI.skin.label);
        Rect r = GUILayoutUtility.GetRect(width, 24f, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;
        var iconRect = Menu.PixelSnap(new Rect(r.x, r.center.y - 7f, 14f, 14f));
        Color previous = GUI.color;
        GUI.color = new Color(colour.r, colour.g, colour.b, previous.a);
        GUI.DrawTexture(iconRect, icon);
        GUI.color = previous;
        float rightWidth = right != null ? TextCache.Width(RowText.StripTags(right), style) + 4f : 0f;
        Widgets.Paint(new Rect(iconRect.xMax + 8f, r.y, r.width - 22f - rightWidth - 8f, r.height), text, style);
        if (right != null) Widgets.Paint(new Rect(r.xMax - rightWidth, r.y, rightWidth, r.height), right, style);
    }

    // A line under an icon line, lined up with its text; long ones wrap.
    private static void Indented(string text)
    {
        float width = PageView.BlockWidth - 22f;
        GUIStyle style = Style(() => Theme.Hint, GUI.skin.label);
        Rect r = GUILayoutUtility.GetRect(width, Controls.HintHeight(text, width) + 4f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(r.x + 22f, r.y, r.width - 22f, r.height), text, style);
    }

    // The page's lines by kind (mod, overlap, known), each kind at least one line; notes (a known mod's explanation)
    // are drawn muted and left out of the bridge's lines.
    private static IEnumerable<(string Kind, string Text, bool Note)> ModsLines(OverlapReport report)
    {
        foreach (var toggle in new[] { Config.ModConflictNotice, Config.ModConflictWarnings })
            yield return ("switch", $"{toggle.Label} value={(toggle.Value ? "on" : "off")}", false);
        foreach (string line in report.ModLines) yield return ("mod", line, false);
        if (!report.Mods.Any(m => !m.Ours)) yield return ("mod", "No other mods.", false);
        if (report.Overlaps.Count == 0) yield return ("overlap", "None.", false);
        foreach (var overlap in report.Overlaps)
        {
            string ours = overlap.OursApplied ? overlap.Feature : $"{overlap.Feature}, applied when {overlap.Feature} needs it";
            yield return ("overlap", $"{overlap.Method} ({ours}): {overlap.OthersText}", false);
        }
        if (report.Known.Count == 0) yield return ("known", "None.", false);
        foreach (var known in report.Known)
        {
            yield return ("known", known.Line, false);
            yield return ("known", known.Text, true);
        }
    }

    // Text across the block in the label colour, wrapped.
    private static void Line(string text)
    {
        float width = PageView.BlockWidth;
        Rect r = GUILayoutUtility.GetRect(width, Controls.HintHeight(text, width) + 4f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) Widgets.Paint(r, text, Style(() => Theme.Label, GUI.skin.label));
    }

    // Muted text across the block, not indented (Hint indents a feature's details under its name).
    private static void Note(string text)
    {
        float width = PageView.BlockWidth;
        Rect r = GUILayoutUtility.GetRect(width, Controls.HintHeight(text, width) + 4f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) Widgets.Paint(r, text, Style(() => Theme.Hint, GUI.skin.label));
    }

    private static void CopyReport(string file)
    {
        string text;
        try
        {
            text = Reports.Read(file);
        }
        catch (Exception e)
        {
            Log.WarnThrottled("diagnostics:report", $"Crash report {file} could not be read: {e.Message}");
            Toasts.Show("diagnostics", "Copy failed; the report could not be read.", ToastKind.Warning);
            return;
        }
        Copy(text, "Report copied.");
    }

    private static void FeatureRow(FeatureDiagnostics f)
    {
        // Dev builds list the compat self-test, which fails on purpose; it must not read as a real problem.
        bool selfTest = f.Id == "selftest";
        var feature = FeatureHost.Find(f.Id);
        StatusState state = f.State == FeatureState.Running ? f.Status.State : StatusState.Off;
        Color color = f.State is FeatureState.Faulted or FeatureState.Unavailable ? Theme.Pause : Theme.StateColor(state);
        Rect row = GUILayoutUtility.GetRect(10f, RowLayout.ControlHeight + 2f, GUILayout.ExpandWidth(true));
        float x = row.x;
        if (Event.current.type == EventType.Repaint)
        {
            Menu.DrawState(new Rect(x, Mathf.Round(row.center.y - 4f), 8f, 8f), state, color);
            Widgets.Paint(new Rect(x + 16f, row.y, 160f, RowLayout.ControlHeight), selfTest ? "Compat self-test (dev)" : f.Name, Style(() => Theme.Label, GUI.skin.label));
            Widgets.Paint(new Rect(x + 180f, row.y, row.width - 330f, RowLayout.ControlHeight), Colored(Capitalized(f.StateText), Theme.LightText(color)), Style(() => Theme.Label, GUI.skin.label));
        }
        float right = row.xMax;
        if (feature != null && feature.CanTurnOff)
        {
            bool on = FeatureSwitches.IsOn(feature);
            right -= Controls.SwitchWidth;
            if (Controls.Switch(new Rect(right, row.y, Controls.SwitchWidth, RowLayout.ControlHeight), on, "feature." + f.Id, $"Turn {f.Name} on or off")  != on)
                Actions.Run($"feature.toggle.{f.Id}", "menu features");
        }
        else if (Event.current.type == EventType.Repaint)
        {
            GUIStyle dim = Style(() => Theme.FieldDim, GUI.skin.label);
            float w = TextCache.Width("always on", dim) + dim.padding.horizontal;
            right -= w;
            Widgets.Paint(new Rect(right, row.y, w, RowLayout.ControlHeight), "always on", dim);
        }
        // A restart can't bring back what the game no longer has, or a feature turned off or waiting for another.
        if (feature != null && feature.State == FeatureState.Faulted)
        {
            float w = Controls.ButtonWidth("Restart");
            right -= w + 8f;
            if (Controls.Button(new Rect(right, row.y, w, RowLayout.ControlHeight), "Restart", "restart." + f.Id)) Actions.Run($"feature.restart.{f.Id}", "menu features");
        }
        if (selfTest) Hint("Nothing is wrong: this test asks for game members that don't exist, to show how a missing member is reported.");
        if (f.State == FeatureState.Running) Hint($"Frame time: {f.FrameText}.");
        foreach (string detail in f.Details) Hint(Colored(Capitalized(detail), Theme.Pause));
        foreach (string edit in f.Edits) Hint("Edit: " + edit);
        GUILayout.Space(6f);
    }

    private static void Hint(string text)
    {
        float width = PageView.BlockWidth - 16f;
        Rect r = GUILayoutUtility.GetRect(width, Controls.HintHeight(text, width), GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) Widgets.Paint(new Rect(r.x + 16f, r.y, r.width - 16f, r.height), text, Style(() => Theme.Hint, GUI.skin.label));
    }

    private static string Capitalized(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
}
