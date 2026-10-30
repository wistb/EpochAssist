using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The dev build's tool window: a small draggable window, apart from the mod's menu so pages keep their real look,
/// holding the sections of whatever interface is in use (the Stash page's reset, say). Each section says when it shows;
/// with none showing the window is hidden. Presses on it are kept from the game as the HUD's are.
/// </summary>
internal static class DevWindow
{
    /// <summary>A part of the window: its title, when it shows, and its GUILayout drawing.</summary>
    internal sealed record Section(string Title, Func<bool> Shown, Action Draw);

    private const int WindowId = 0x5EA0D;
    private static readonly List<Section> s_sections = new();
    private static readonly List<Section> s_shown = new();
    private static Rect s_rect = new(-1f, 140f, 300f, 60f);
    private static GUIStyle? s_style;
    private static bool s_visible;

    public static void Add(Section section)
    {
        s_sections.RemoveAll(s => s.Title == section.Title);
        s_sections.Add(section);
    }

    public static void Remove(string title) => s_sections.RemoveAll(s => s.Title == title);

    public static void Start() => InputGate.ToolHitTest = point => s_visible && s_rect.Contains(point);

    public static void Shutdown()
    {
        InputGate.ToolHitTest = null;
        s_sections.Clear();
        s_visible = false;
        s_style = null;
    }

    public static void OnGUI()
    {
        if (Event.current.type == EventType.Layout)
        {
            s_shown.Clear();
            foreach (var section in s_sections)
                if (Safe.Call(section.Shown, false, "devwindow.shown")) s_shown.Add(section);
            s_visible = s_shown.Count > 0;
        }
        if (!s_visible) return;
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        // At the right of the screen to start, clear of the menu.
        if (s_rect.x < 0f) s_rect.x = Screen.width - s_rect.width - 24f;
        // Solid, as the mod's popups are.
        s_style ??= new GUIStyle(GUI.skin.window) { normal = { background = Theme.PopupBg, textColor = Color.white }, onNormal = { background = Theme.PopupBg } };
        // Height follows the content each layout pass.
        s_rect = GUILayout.Window(WindowId, s_rect, (GUI.WindowFunction)Draw, "Dev", s_style, GUILayout.Width(300f));
        // Over the menu while the pointer is on it: windows otherwise stack by focus.
        if (Event.current.type == EventType.Repaint && s_rect.Contains(Event.current.mousePosition)) GUI.BringWindowToFront(WindowId);
        s_rect.x = Mathf.Clamp(s_rect.x, 0f, Screen.width - 60f);
        s_rect.y = Mathf.Clamp(s_rect.y, 0f, Screen.height - 30f);
        GUI.matrix = matrix;
    }

    private static void Draw(int id)
    {
        foreach (var section in s_shown)
        {
            GUILayout.Label(section.Title, GUI.skin.label);
            try
            {
                section.Draw();
            }
            catch (Exception e)
            {
                GUILayout.Label("error: " + e.Message);
                Log.ErrorThrottled("devwindow:" + section.Title, $"Dev window {section.Title} failed: {e.Message}", 30);
            }
            GUILayout.Space(6f);
        }
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    // ---- Helpers for sections ----

    /// <summary>A button; greyed with the reason as its text's tail when it can't act.</summary>
    public static bool Button(string label, string? blocked = null)
    {
        bool was = GUI.enabled;
        GUI.enabled = was && blocked == null;
        bool pressed = GUILayout.Button(blocked == null ? label : $"{label} ({blocked})");
        GUI.enabled = was;
        return pressed;
    }

    public static void Text(string text)
    {
        if (text.Length > 0) GUILayout.Label(text);
    }
}

/// <summary>Runs the dev tool window and the work its sections start (the stash reset and scramble).</summary>
internal sealed class DevWindowFeature : Feature
{
    public override string Id => "devwindow";
    public override string Name => "Dev window";
    // Above the menu, so it stays usable while a page is open.
    public override GuiLayer Layer => GuiLayer.Menu;
    // A dev tool: its scramble and reset read the stash while they wait on the game.
    public override double BudgetMs => 2.0;

    public override void Start()
    {
        DevWindow.Start();
        StashDevSection.Register();
    }

    public override void Update()
    {
        StashReset.Frame();
        StashScramble.Frame();
    }
    public override void OnGUI() => DevWindow.OnGUI();
    public override void Shutdown() => DevWindow.Shutdown();
}
