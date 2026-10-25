using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// Diagnostics, shown as the Settings page's Diagnostics tab: the mod's build and mode, the log level, crash reports, one
/// card per feature (state, errors, missing game members, frame time), problems, and a copy of it all as scrubbed text
/// for a bug report. The feature owns the copy and open actions.
/// </summary>
internal sealed class DiagnosticsFeature : Feature
{
    public override string Id => "diagnostics";
    public override string Name => "Diagnostics";

    public override void Start()
    {
        Actions.Register(Id, "diagnostics.copy", "Copy diagnostics", () => DiagnosticsPage.Copy(Diagnostics.Build().ToText(), "Diagnostics copied."));
        Actions.Register(Id, "diagnostics.open", "Open diagnostics", () => Menu.OpenSettingsTab(Config.Diagnostics));
    }

    public override void Shutdown() => DiagnosticsPage.Reset();
}

internal static class DiagnosticsPage
{
    private const float RebuildSeconds = 0.5f;

    private static DiagnosticsSnapshot? s_snapshot;
    private static float s_builtAt = float.MinValue;
    private static bool s_clearing;
    private static Action? s_pending;

    public static void Reset()
    {
        s_snapshot = null;
        s_builtAt = float.MinValue;
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

    private static readonly string[] s_tabs = { "Overview", "Features", "Reports", "Console" };
    private const int ConsoleTab = 3;
    private static int s_tab;

    public static void ShowConsole() => Tab = ConsoleTab;

    /// <summary>Diagnostics' own pages, listed under it in the sidebar while Settings is open.</summary>
    public static IReadOnlyList<string> Tabs => s_tabs;

    /// <summary>The page of Diagnostics shown.</summary>
    public static int Tab
    {
        get => s_tab;
        set
        {
            if (value == s_tab) return;
            s_tab = Math.Clamp(value, 0, s_tabs.Length - 1);
            s_clearing = false;
        }
    }

    /// <summary>The Settings page's Diagnostics: the page of it picked in the sidebar.</summary>
    public static void DrawTab()
    {
        if (s_tab == ConsoleTab)
        {
            ConsolePage.Draw();
            return;
        }
        Menu.BeginScroll();
        Draw();
        Menu.EndScroll();
    }

    private static void Draw()
    {
        if (s_pending != null && Event.current.type == EventType.Layout)
        {
            var pending = s_pending;
            s_pending = null;
            pending();
        }

        // Rebuilt in a layout pass only, so the passes of one frame draw the same thing.
        if (s_snapshot == null || (Event.current.type == EventType.Layout && Time.unscaledTime - s_builtAt >= RebuildSeconds))
        {
            s_snapshot = Diagnostics.Build();
            s_builtAt = Time.unscaledTime;
        }
        var snapshot = s_snapshot;

        switch (s_tab)
        {
            case 0:
                ModCard(snapshot);
                ProblemsCard(snapshot);
                SettingsView.Card(Config.Diagnostics, "Logging");
                break;
            case 1:
                foreach (var feature in snapshot.Features) FeatureCard(feature);
                break;
            default:
                ReportsCard();
                break;
        }
    }

    // The mod's own card, with Copy diagnostics on its title row.
    private static void ModCard(DiagnosticsSnapshot s)
    {
        GUILayout.BeginVertical(Style(() => Theme.Card, GUI.skin.box));
        GUILayout.BeginHorizontal();
        GUILayout.Label(BuildInfo.Name.ToUpperInvariant(), Style(() => Theme.CardTitle, GUI.skin.label));
        GUILayout.FlexibleSpace();
        // Copies even while the diagnostics feature is down, when its action is gone.
        if (Pressed(GUILayout.Button("Copy diagnostics", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)), "copy")
            && Actions.Run("diagnostics.copy", "menu button").StartsWith("error"))
            Copy(Diagnostics.Build().ToText(), "Diagnostics copied.");
        GUILayout.EndHorizontal();
        Hint($"Version {BuildInfo.Version}, build {Runtime.BuildId}{(Runtime.Hosted ? ", dev build" : string.Empty)}.");
        Hint($"Tested game version: {s.TestedGameVersion}.");
        Hint(s.Mode == "not in game" ? $"Not in game; scene '{s.Scene}'." : $"Playing {s.Mode} in '{s.Scene}' ({s.SceneKind}).");
        Hint($"Frame time for the whole mod: {s.ModFrameText}.");
        Hint($"Changes to the game: {s.ChangesText}.");
        foreach (string edit in s.Edits) Hint(Colored(edit, Theme.Dim));
        Hint($"Profile: {s.Profiles}.");
        GUILayout.Space(4f);
        Hint(Colored("Copy diagnostics puts all of these tabs on the clipboard for a bug report, without personal data.", Theme.Dim));
        EndCard();
    }

    // Only when there is something to say: settings that couldn't be updated, file problems.
    private static void ProblemsCard(DiagnosticsSnapshot s)
    {
        if (s.SettingsFailures.Count == 0 && s.ProblemFiles.Count == 0) return;
        BeginCard("PROBLEMS");
        foreach (string line in s.SettingsFailures.Concat(s.ProblemFiles)) Hint(Colored(line, Theme.Pause));
        EndCard();
    }

    // Newest first, each with its own copy button.
    private static void ReportsCard()
    {
        var reports = Reports.List();
        if (reports.Count > 0) ClearRow(reports.Count);
        BeginCard("CRASH REPORTS");
        if (reports.Count == 0) Hint("No reports.");
        foreach (var report in reports)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{report.Time}  ·  {report.Feature}{(report.Count > 1 ? $"  ·  {report.Count} times" : string.Empty)}", Style(() => Theme.RowLabel, GUI.skin.label), GUILayout.Height(26f));
            GUILayout.FlexibleSpace();
            if (Pressed(GUILayout.Button("Copy", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)), "report." + report.File))
                CopyReport(report.File);
            GUILayout.EndHorizontal();
        }
        EndCard();
    }

    // Above the card, so it is in view however long the list is; Clear asks before deleting.
    private static void ClearRow(int count)
    {
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        GUILayout.BeginHorizontal();
        if (!s_clearing)
        {
            Hint("Saved in UserData/EpochAssist/reports. Nothing is sent anywhere.");
            GUILayout.FlexibleSpace();
            if (Pressed(GUILayout.Button("Clear", button, GUILayout.ExpandWidth(false)), "reports.clear")) s_clearing = true;
        }
        else
        {
            Hint(Colored($"Delete {(count == 1 ? "the report" : $"all {count} reports")}?", Theme.Pause) + "\nThey can't be brought back.");
            GUILayout.FlexibleSpace();
            // Run in the next layout pass, so this frame's passes still draw the same list.
            if (Pressed(GUILayout.Button("Delete", button, GUILayout.ExpandWidth(false)), "reports.clear.confirm"))
                s_pending = () =>
                {
                    s_clearing = false;
                    if (Reports.Clear(out string reason)) Toasts.Show("diagnostics", "Reports cleared.", ToastKind.Info);
                    else Toasts.Show("diagnostics", $"Reports not cleared: {reason}.", ToastKind.Warning);
                };
            if (Pressed(GUILayout.Button("Keep", button, GUILayout.ExpandWidth(false)), "reports.clear.keep")) s_clearing = false;
        }
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);
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

    private static void FeatureCard(FeatureDiagnostics f)
    {
        // Dev builds list the compat self-test, which fails on purpose; it must not read as a real problem.
        bool selfTest = f.Id == "selftest";
        BeginCard(selfTest ? "COMPAT SELF-TEST (DEV BUILDS ONLY)" : f.Name.ToUpperInvariant());
        if (selfTest) Hint("Nothing is wrong: this test asks for game members that don't exist, to show how a missing member is reported.");
        StatusState state = f.State == FeatureState.Running ? f.Status.State : StatusState.Off;
        Color color = f.State is FeatureState.Faulted or FeatureState.Unavailable ? Theme.Pause : Theme.StateColor(state);
        GUILayout.BeginHorizontal();
        Rect shape = GUILayoutUtility.GetRect(16f, 20f, GUILayout.Width(16f), GUILayout.Height(20f));
        if (Themed && Event.current.type == EventType.Repaint) Menu.DrawState(new Rect(shape.x, shape.center.y - 4f, 8f, 8f), state, color);
        GUILayout.Label(Colored(Capitalized(f.StateText), color), Style(() => Theme.Hint, GUI.skin.label));
        GUILayout.EndHorizontal();
        if (f.State == FeatureState.Running) Hint($"Frame time: {f.FrameText}.");
        foreach (string detail in f.Details) Hint(Colored(Capitalized(detail), Theme.Pause));
        foreach (string edit in f.Edits) Hint("Edit: " + edit);
        EndCard();
    }

    private static string Capitalized(string text) => text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
}
