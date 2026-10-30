using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// Config › General › Modules: a card per module with its state and a Load or Unload button. Changes wait in a bar at the
/// bottom that names them; Apply asks once, stores them, and they take effect at the next start. Until the first-start
/// choice is made it is a form instead: a check box per module, Select all, Clear and Load selected, which loads the
/// selected modules at once.
/// </summary>
internal static class ModulesPage
{
    public const string Block = "modules";

    private const string Intro = "Which parts of Epoch Assist exist. Unloaded modules show no pages, have no cost, and disconnect from the HUD. Their settings are kept. Changes apply at the next game start.";
    private const string UnavailableTag = "unavailable after a game update";
    private const string Later = "This can be changed later in Config › General › Modules.";
    private const string Waiting = "Modules are selected after entering a character.";
    private const float Pad = 12f, CardGap = 8f, Edge = 3f;

    // The first-start form's selection: page state until Load selected stores them.
    private static readonly HashSet<string> s_ticked = new();
    // Load (true) or unload (false) changes waiting for Apply, by module id.
    private static readonly Dictionary<string, bool> s_staged = new();


    public static void Register() => MenuBlocks.Register(Block, (rect, _) => Draw(rect), _ => BridgeLines(), (_, width) => Height(width));

    public static void Reset()
    {
        s_ticked.Clear();
        s_staged.Clear();
        s_hadCharacter = false;
    }

    private static bool s_hadCharacter;

    /// <summary>Each frame (the menu's update): while the first-start choice is open, every character entry says how to
    /// make it.</summary>
    public static void OnUpdate()
    {
        bool has = Profiles.HasCharacter;
        if (has && !s_hadCharacter && FirstStart) ShowToast();
        s_hadCharacter = has;
    }

    /// <summary>The first-start toast, which stays until dismissed or the menu opens.</summary>
    public static void ShowToast() =>
        Toasts.Show("modules", $"{BuildInfo.Name}: press {KeySettings.MenuKey.Display} to select modules", ToastKind.Info, sticky: true);

    /// <summary>Where the menu opens while the first-start choice is open and a character is in play; null otherwise.</summary>
    public static PageNode? FirstStartPage => FirstStart && Profiles.HasCharacter ? MenuRoutes.Resolve(MenuRoutes.Modules) : null;

    // The choice is open; it is made in a character, so before one the list shows without controls.
    private static bool FirstStart => Modules.Pending;
    private static bool Form => FirstStart && Profiles.HasCharacter;
    private static string? TopLine => Form ? Later : FirstStart ? Waiting : null;

    // ---- Layout: the intro card, a card per module, then the changes bar or the form's buttons ----

    // The label's look, wrapping (the intro, the changes); made again when the theme rebuilds.
    private static GUIStyle? s_words, s_wordsFrom;
    private static GUIStyle Words
    {
        get
        {
            var from = Style(() => Theme.Label, GUI.skin.label);
            if (s_words == null || s_wordsFrom != from) (s_words, s_wordsFrom) = (new GUIStyle(from) { wordWrap = true }, from);
            return s_words;
        }
    }
    private static GUIStyle Hint => Style(() => Theme.Hint, GUI.skin.label);
    // Bold, as the tree's nodes: each module is a part of the mod, not a setting.
    private static GUIStyle Title => Style(() => Theme.TreeNode, GUI.skin.label);

    private static float Inner(float width) => width - 2f * Pad - Edge;

    private static string IntroText => Form ? $"{Intro} {Later}" : FirstStart ? $"{Intro} {Waiting}" : Intro;

    private static float IntroHeight(float width) => 2f * Pad + TextCache.Height(IntroText, Words, Inner(width));

    private static float CardHeight(Module module, float width) =>
        2f * Pad + RowLayout.ControlHeight + TextCache.Height(module.Description, Hint, Inner(width));

    private static float BarHeight(float width) =>
        s_staged.Count == 0 || FirstStart ? 0f : CardGap + 2f * Pad + TextCache.Height(ChangesText, Words, Inner(width)) + 6f + RowLayout.ControlHeight;

    private static float Height(float width)
    {
        float h = IntroHeight(width) + CardGap + Modules.All.Sum(m => CardHeight(m, width) + CardGap) + BarHeight(width);
        if (Form) h += RowLayout.ControlHeight + 4f;
        return h + 4f;
    }

    private static void Draw(Rect rect)
    {
        // Changes the stored choices already match (a reload, the dev tools) are none.
        foreach (string id in s_staged.Where(p => p.Value == ModuleChoices.Choice(p.Key)).Select(p => p.Key).ToList()) s_staged.Remove(id);
        float y = rect.y;
        var intro = new Rect(rect.x, y, rect.width, IntroHeight(rect.width));
        if (Event.current.type == EventType.Repaint)
        {
            Card(intro, Theme.Accent);
            Paint(new Rect(intro.x + Pad + Edge, intro.y + Pad, Inner(rect.width), intro.height - 2f * Pad), IntroText, Words);
        }
        y = intro.yMax + CardGap;
        foreach (var module in Modules.All) y = ModuleCard(module, rect.x, y, rect.width) + CardGap;
        if (Form) Buttons(rect.x, y, rect.width);
        else if (!FirstStart && s_staged.Count > 0) ChangesBar(new Rect(rect.x, y, rect.width, BarHeight(rect.width) - CardGap));
    }

    // The card's panel, with a coloured edge at its left when it stands out (a change waiting, a selected module).
    private static void Card(Rect r, Color? edge)
    {
        if (!Themed) return;
        Theme.Card.Draw(r, GUIContent.none, false, false, false, false);
        if (edge is { } colour) Theme.Fill(new Rect(r.x, r.y + 6f, Edge, r.height - 12f), colour);
    }

    // Name and state on the first line with the button (or check box) at the right; the description under them.
    private static float ModuleCard(Module module, float x, float y, float width)
    {
        var card = new Rect(x, y, width, CardHeight(module, width));
        string key = "modules." + module.Id;
        bool ticked = Form && s_ticked.Contains(module.Id);
        bool staged = s_staged.ContainsKey(module.Id);
        // Selected for the first start in the accent; a change waiting for Apply in the alert amber.
        if (Event.current.type == EventType.Repaint) Card(card, staged ? Controls.AlertColor : ticked ? Theme.Accent : null);
        float left = card.x + Pad + Edge;
        var line = new Rect(left, card.y + Pad, Inner(width), RowLayout.ControlHeight);
        float right = line.xMax;

        if (Form)
        {
            var box = new Rect(right - 16f, line.y, 16f, line.height);
            bool hover = GUI.enabled && card.Contains(Event.current.mousePosition);
            Controls.Check(box, ticked ? 1 : 0, hover);
            bool clicked = GUI.Button(card, GUIContent.none, GUIStyle.none);
            if (Focus.Item(key, box) || clicked)
            {
                if (ticked) s_ticked.Remove(module.Id);
                else s_ticked.Add(module.Id);
            }
            Popups.Tip(box, $"Load {module.Name}", key);
            right = box.x - 12f;
        }
        else if (!FirstStart)
        {
            bool target = s_staged.TryGetValue(module.Id, out bool load) ? load : ModuleChoices.Choice(module.Id);
            string label = staged ? "Undo" : target ? "Unload" : "Load";
            float w = Mathf.Max(Controls.ButtonWidth("Unload"), Controls.ButtonWidth(label));
            var button = new Rect(right - w, line.y, w, line.height);
            string tip = staged ? "Keeps it as it is" : target ? $"Unload {module.Name} at the next start" : $"Load {module.Name} at the next start";
            if (Controls.Button(button, label, key, tip: tip))
            {
                if (staged) s_staged.Remove(module.Id);
                else s_staged[module.Id] = !target;
            }
            right = button.x - 12f;
        }

        float nameWidth = Mathf.Min(TextCache.Width(module.Name, Title) + 4f, right - left);
        if (Event.current.type == EventType.Repaint) Paint(new Rect(left, line.y, nameWidth, line.height), module.Name, Title);
        float tagX = left + nameWidth + 12f;
        foreach (var (text, state) in Tags(module))
        {
            float w = Controls.StatePillWidth(text, full: true);
            if (tagX + w > right) break;
            // The state this start has in its own colour; what the next start changes is an alert.
            Controls.StatePill(new Rect(tagX, line.y, w, line.height), text, state, full: true, tint: state is StatusState.Holding or StatusState.Paused ? Controls.AlertColor : null);
            tagX += w + 6f;
        }
        if (!FirstStart && Unavailable(module)) Unavailability(key, line.y, tagX, right);

        float descHeight = TextCache.Height(module.Description, Hint, Inner(width));
        if (Event.current.type == EventType.Repaint) Paint(new Rect(left, line.yMax + 2f, Inner(width), descHeight), module.Description, Hint);
        return card.yMax;
    }

    // The warning's shape and words in full (a state pill would cut them short), and the way to what is missing.
    private static void Unavailability(string key, float y, float x, float right)
    {
        GUIStyle style = Style(() => Theme.FieldText, GUI.skin.label);
        float w = Mathf.Min(16f + TextCache.Width(UnavailableTag, style), right - x);
        if (Event.current.type == EventType.Repaint)
        {
            Color colour = Theme.LightText(Controls.AlertColor);
            Menu.DrawState(new Rect(x, Mathf.Round(y + RowLayout.ControlHeight / 2f - 4f), 8f, 8f), StatusState.Paused, colour);
            Paint(new Rect(x + 14f, y, w - 14f, RowLayout.ControlHeight), Colored(UnavailableTag, colour), style);
        }
        x += w + 8f;
        float bw = Controls.ButtonWidth("Details");
        if (x + bw <= right && Controls.Button(new Rect(x, y, bw, RowLayout.ControlHeight), "Details", key + ".details", tip: "Opens Diagnostics › Features: what the game no longer has"))
            Menu.Open(MenuRoutes.Features);
    }

    // The state this start has, then what the next start does differently: a stored change, or one waiting for Apply.
    private static IEnumerable<(string Text, StatusState State)> Tags(Module module)
    {
        if (Form) yield break;
        bool loaded = Modules.Loaded(module.Id);
        yield return loaded ? ("Loaded", StatusState.Active) : ("Not loaded", StatusState.Off);
        if (FirstStart) yield break;
        if (s_staged.TryGetValue(module.Id, out bool load)) yield return (load ? "Will load" : "Will unload", StatusState.Holding);
        else if (Modules.RestartNeeded(module.Id)) yield return (ModuleChoices.Choice(module.Id) ? "Loads at next start" : "Unloads at next start", StatusState.Paused);
    }

    // This start's check of the game found something missing.
    private static bool Unavailable(Module module) => FeatureHost.Find(module.Id) is { State: FeatureState.Unavailable };

    // The waiting changes by what they do, each verb once: "Unload: Macros and Stash" on one line, "Load: Planner" on the
    // next; "every module" when a verb covers them all.
    private static string ChangesText
    {
        get
        {
            var lines = new List<string>();
            if (Names(false) is { } unload) lines.Add("Unload: " + unload);
            if (Names(true) is { } load) lines.Add("Load: " + load);
            return string.Join("\n", lines);
        }
    }

    // The staged modules with this change, as "A, B and C" (or "every module"); null for none.
    private static string? Names(bool load)
    {
        var names = Modules.All.Where(m => s_staged.TryGetValue(m.Id, out bool l) && l == load).Select(m => m.Name).ToList();
        if (names.Count == 0) return null;
        if (names.Count == Modules.All.Count) return "every module";
        return names.Count == 1 ? names[0] : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";
    }

    // "Unload Macros and Stash, and load Planner, at the next game start?", then what unloading means when it happens.
    private static string Question()
    {
        var parts = new List<string>();
        if (Names(false) is { } unload) parts.Add("unload " + unload);
        if (Names(true) is { } load) parts.Add("load " + load);
        string what = string.Join(", and ", parts);
        string ask = $"{char.ToUpperInvariant(what[0])}{what[1..]}{(parts.Count > 1 ? "," : string.Empty)} at the next game start?";
        return Names(false) != null ? ask + " Unloaded modules show no pages, have no cost, and disconnect from the HUD. Their settings are kept." : ask;
    }

    private static readonly ActionButton s_apply = new("Apply…", "modules.apply", primary: true)
    {
        Prompt = new ButtonPrompt(Question,
            "Apply", _ =>
            {
                Apply();
                return null;
            }),
    };

    // The waiting changes named, with Discard and Apply (which asks first).
    private static void ChangesBar(Rect bar)
    {
        if (Event.current.type == EventType.Repaint)
        {
            Card(bar, Controls.AlertColor);
            Paint(new Rect(bar.x + Pad + Edge, bar.y + Pad, Inner(bar.width), TextCache.Height(ChangesText, Words, Inner(bar.width))), ChangesText, Words);
        }
        float h = RowLayout.ControlHeight, y = bar.yMax - Pad - h;
        float apply = Controls.ButtonWidth(s_apply.Label), discard = Controls.ButtonWidth("Discard");
        var applyAt = new Rect(bar.xMax - Pad - apply, y, apply, h);
        if (Controls.Button(applyAt, s_apply, "modules.apply", "Stores these changes for the next game start")) Apply();
        if (Controls.Button(new Rect(applyAt.x - 8f - discard, y, discard, h), "Discard", "modules.discard", tip: "Drops these changes")) s_staged.Clear();
    }

    private static void Apply()
    {
        foreach (var (id, load) in s_staged)
            if (ModuleChoices.For(id) is { } choice) choice.Value = load;
        s_staged.Clear();
        Config.Save();
        Toasts.Show("modules", "Module changes apply at the next game start.", ToastKind.Info);
    }

    private static void Buttons(float x, float y, float width)
    {
        float h = RowLayout.ControlHeight;
        float load = Controls.ButtonWidth("Load selected"), all = Controls.ButtonWidth("Select all"), clear = Controls.ButtonWidth("Clear");
        if (Controls.Button(new Rect(x, y, all, h), "Select all", "modules.selectall", tip: "Selects every module"))
            foreach (var module in Modules.All) s_ticked.Add(module.Id);
        if (Controls.Button(new Rect(x + all + 8f, y, clear, h), "Clear", "modules.clear", tip: "Clears the selection")) s_ticked.Clear();
        if (Controls.Button(new Rect(x + width - load, y, load, h), "Load selected", "modules.load", primary: true, tip: "Loads the selected modules now; the others stay out"))
        {
            var chosen = s_ticked.ToList();
            s_ticked.Clear();
            // In the next update, not in the middle of drawing: starting features changes the host's list.
            Scheduler.HostAfter("modules load", 0f, () => Modules.LoadPending(chosen));
        }
    }

    private static IEnumerable<string> BridgeLines()
    {
        yield return Form ? "form first-start" : FirstStart ? "form waiting" : "form normal";
        foreach (var module in Modules.All)
        {
            string choice = Form ? (s_ticked.Contains(module.Id) ? "ticked" : "unticked") : ModuleChoices.Choice(module.Id) ? "on" : "off";
            var tags = Tags(module).Select(t => t.Text).Concat(Unavailable(module) ? new[] { UnavailableTag } : Array.Empty<string>()).ToList();
            yield return $"module {module.Id} {RowText.Quote(module.Name)} {choice} {(Modules.Loaded(module.Id) ? "loaded" : "not-loaded")}{(tags.Count > 0 ? " " + RowText.Quote(string.Join(", ", tags)) : string.Empty)}";
        }
        if (s_staged.Count > 0 && !FirstStart) yield return "changes " + RowText.Quote(ChangesText.Replace("\n", "; "));
    }
}
