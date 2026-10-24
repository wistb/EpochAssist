using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// The console, shown as the Console tab of Settings → Diagnostics: type a command from the registry (help lists them) and read
/// its reply. Dev commands answer only under the dev loader. Opened from the tab or by console.open (the console key),
/// which also puts the keyboard in its input line.
/// </summary>
internal sealed class ConsoleFeature : Feature
{
    public override string Id => "console";
    public override string Name => "Console";

    public override void Start()
    {
        ConsolePage.Start();
        Actions.Register(Id, "console.open", "Open the console", Menu.OpenConsole);
    }

    public override void Update() => ConsolePage.Update();
    public override void Shutdown() => ConsolePage.Shutdown();
}

internal static class ConsolePage
{
    private const string FieldControl = "EpochAssistConsole";
    private const int MaxEntries = 200, MaxHistory = 20, MaxLine = 256;
    private const float OutputHeight = 250f;

    // An entry is a line typed and its reply; each part has a kind for its colour.
    private enum Kind { Input, Ok, Error, Text }

    private sealed class Run
    {
        public readonly List<List<(Kind Kind, string Text)>> Output = new();
        public readonly List<string> History = new();
        public int Recall = -1;
        public string Input = string.Empty;
        public (Func<string?> Reply, string Line, List<(Kind, string)> Entry)? Pending;
        public Vector2 Scroll;
        public bool AtBottom = true, ToBottom, Submit;
        public float ContentHeight, ViewHeight;
    }

    private static Run? s_run;
    private static GUIStyle? s_lineStyle;
    private static GUIStyle? s_lineBase;

    public static void Start() => s_run = new Run();

    public static void Shutdown()
    {
        s_run = null;
        Menu.FocusConsoleField = false;
    }

    // A pending reply (a command that finishes later) is asked once a frame.
    public static void Update()
    {
        if (s_run?.Pending is not { } pending) return;
        if (Commands.Poll(pending.Reply, pending.Line) is not { } reply) return;
        s_run.Pending = null;
        AddReply(s_run, pending.Entry, reply);
    }

    public static void Draw()
    {
        var run = s_run;
        if (run == null || FeatureHost.Find("console")?.State != FeatureState.Running)
        {
            Hint(FeatureHost.Find("console") is { } console ? Menu.Reason(console) : "The console isn't running.");
            return;
        }

        // A line runs at the start of a layout pass, so every pass of a frame draws the same output.
        Event e = Event.current;
        if (run.Submit && e.type == EventType.Layout)
        {
            run.Submit = false;
            Submit(run);
        }

        bool focused = GUI.GetNameOfFocusedControl() == FieldControl;
        // Keys meant for the input line, taken before the field sees them: Enter runs, Up and Down walk the history.
        if (focused && e.type == EventType.KeyDown)
        {
            if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                run.Submit = true;
                e.Use();
            }
            else if (e.keyCode is KeyCode.UpArrow or KeyCode.DownArrow)
            {
                Recall(run, e.keyCode == KeyCode.UpArrow ? -1 : 1);
                e.Use();
            }
        }

        DrawOutput(run);
        GUILayout.Space(6f);

        GUILayout.BeginHorizontal();
        Rect field = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true), GUILayout.Height(26f));
        GUI.SetNextControlName(FieldControl);
        run.Input = GUI.TextField(field, run.Input, Style(() => Theme.SearchField, GUI.skin.textField));
        if (focused) Focus.Take("console.field");
        if (Focus.Item("console.field", field) || Focus.Arrived("console.field") || (Menu.FocusConsoleField && e.type == EventType.Repaint))
        {
            Menu.FocusConsoleField = false;
            GUI.FocusControl(FieldControl);
        }
        if (Themed && e.type == EventType.Repaint && run.Input.Length == 0) GUI.Label(field, "Type a command, or help", Theme.Placeholder);
        // Room for the field's focus outline.
        GUILayout.Space(8f);
        if (Pressed(GUILayout.Button("Run", Style(() => Theme.SmallButton, GUI.skin.button), GUILayout.ExpandWidth(false)), "console.run")) run.Submit = true;
        GUILayout.EndHorizontal();
        Hint("Enter runs the line; Up and Down bring back earlier lines. clear empties the output.");
    }

    private static void DrawOutput(Run run)
    {
        GUIStyle style = LineStyle();
        if (run.ToBottom && Event.current.type == EventType.Layout)
        {
            run.Scroll.y = float.MaxValue;
            run.ToBottom = false;
        }
        run.Scroll = GUILayout.BeginScrollView(run.Scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, Style(() => Theme.Card, GUI.skin.box),
            GUILayout.Height(OutputHeight));
        Color previous = GUI.contentColor;
        if (run.Output.Count == 0)
        {
            GUI.contentColor = Theme.Dim;
            GUILayout.Label("Replies show here.", style);
        }
        foreach (var entry in run.Output)
        {
            foreach (var (kind, text) in entry)
            {
                GUI.contentColor = kind switch
                {
                    Kind.Input => Theme.Dim,
                    Kind.Ok => Theme.Good,
                    Kind.Error => Theme.Pause,
                    _ => Theme.Text,
                };
                GUILayout.Label(text, style);
            }
        }
        GUI.contentColor = previous;
        GUILayout.Label(GUIContent.none, GUIStyle.none, GUILayout.Height(1f));
        if (Event.current.type == EventType.Repaint) run.ContentHeight = GUILayoutUtility.GetLastRect().yMax;
        GUILayout.EndScrollView();
        if (Event.current.type == EventType.Repaint)
        {
            run.ViewHeight = GUILayoutUtility.GetLastRect().height;
            // Scrolled up to read: new output doesn't pull the view down.
            run.AtBottom = run.Scroll.y >= run.ContentHeight - run.ViewHeight - 8f;
        }
    }

    // Output lines in the body text's size, without rich text, so replies show exactly as written.
    private static GUIStyle LineStyle()
    {
        GUIStyle hint = Style(() => Theme.Hint, GUI.skin.label);
        if (s_lineStyle == null || !ReferenceEquals(s_lineBase, hint))
        {
            s_lineBase = hint;
            s_lineStyle = new GUIStyle(hint) { richText = false, wordWrap = true, margin = new RectOffset(0, 0, 0, 1) };
            s_lineStyle.normal.textColor = Color.white;
        }
        return s_lineStyle;
    }

    private static void Submit(Run run)
    {
        string line = run.Input.Trim();
        run.Input = string.Empty;
        run.Recall = -1;
        if (line.Length == 0) return;
        AddHistory(run, line);
        if (line.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            run.Output.Clear();
            return;
        }

        var entry = new List<(Kind, string)>();
        bool cut = line.Length > MaxLine;
        if (cut) line = line.Substring(0, MaxLine);
        entry.Add((Kind.Input, "> " + line));
        if (cut) entry.Add((Kind.Error, $"error line cut to {MaxLine} characters"));
        AddEntry(run, entry);
        Log.Info("console: > " + line);

        if (Commands.Run(line, "console", Runtime.Hosted) is not { } result) return;
        if (result.Text.Length > 0) AddReply(run, entry, result.Text);
        if (result.Pending != null) run.Pending = (result.Pending, line, entry);
    }

    private static void AddReply(Run run, List<(Kind, string)> entry, string reply)
    {
        // Every reply line goes to the log too, so a console session can be read back from it.
        foreach (string text in reply.Split('\n'))
        {
            entry.Add((text.StartsWith("ok") ? Kind.Ok : text.StartsWith("error") ? Kind.Error : Kind.Text, text));
            Log.Info("console: " + text.TrimEnd('\r'));
        }
        if (reply.StartsWith("error unknown command:")) entry.Add((Kind.Text, "Type help for the list."));
        if (run.AtBottom) run.ToBottom = true;
    }

    private static void AddEntry(Run run, List<(Kind, string)> entry)
    {
        run.Output.Add(entry);
        if (run.Output.Count > MaxEntries) run.Output.RemoveAt(0);
        run.ToBottom = true;
    }

    private static void AddHistory(Run run, string line)
    {
        if (run.History.Count == 0 || run.History[^1] != line) run.History.Add(line);
        if (run.History.Count > MaxHistory) run.History.RemoveAt(0);
    }

    // Up goes to older lines, Down to newer ones; past the newest the line is empty again.
    private static void Recall(Run run, int step)
    {
        if (run.History.Count == 0) return;
        int index = run.Recall < 0 ? run.History.Count : run.Recall;
        index = Math.Clamp(index + step, 0, run.History.Count);
        run.Recall = index == run.History.Count ? -1 : index;
        run.Input = run.Recall < 0 ? string.Empty : run.History[index];
        // The cursor goes to the end of the recalled line.
        if (GUIUtility.GetStateObject(Il2CppInterop.Runtime.Il2CppType.Of<TextEditor>(), GUIUtility.keyboardControl)?.TryCast<TextEditor>() is { } editor)
        {
            editor.text = run.Input;
            editor.MoveTextEnd();
        }
    }
}
