using System.Text.Json.Nodes;
using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// Profiles (beside Settings at the top of the page): a dropdown picks the profile in use, with New, Duplicate, Rename
/// and Delete beside it. In game the pick is the playing character's; out of game it is the profile being edited.
/// Every setting on every page belongs to the profile in use.
/// </summary>
internal static class ProfilesPage
{
    private const string NameControl = "EpochAssistProfileName";

    private static bool s_renaming;
    private static string s_name = string.Empty;
    private static bool s_deleting;
    // Changes run at the start of a layout pass, so every pass of a frame draws the same thing.
    private static Action? s_pending;

    public static void Draw()
    {
        if (s_pending != null && Event.current.type == EventType.Layout)
        {
            var pending = s_pending;
            s_pending = null;
            pending();
        }

        Dropdown.BeginEvent();
        DrawCard();
        ExportCard();
        ImportCard();
        // Always one entry, so the layout passes of a frame match; it grows while a list is open.
        GUILayout.Space(Dropdown.OpenHeight);
        Dropdown.DrawList();
    }

    // ---- Export and import, by segment and by setting ----

    // Chosen settings by key (group/section/setting); every one to start with.
    private static HashSet<string>? s_exportChosen;
    private static HashSet<string> s_importChosen = new();
    private static ProfileExport? s_import;
    private static int s_importSource, s_importTarget;

    private static void ExportCard()
    {
        var segments = ProfileTransfer.Segments;
        s_exportChosen ??= new HashSet<string>(ProfileTransfer.KeysOf(segments.Select(x => x.Id)));
        BeginCard("EXPORT");
        Hint($"Choose what to export from {Profiles.Active.Name}; open a part to pick single settings. The file goes to UserData/EpochAssist/exports and is copied to the clipboard.");
        SettingsTree(segments, s_exportChosen, "export", setting => setting.ToJson(global: false));
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && s_exportChosen.Count > 0;
        if (Pressed(GUILayout.Button("Export", button, GUILayout.ExpandWidth(false)), "export"))
        {
            var chosen = s_exportChosen.ToList();
            s_pending = () => Export(chosen);
        }
        GUI.enabled = wasEnabled;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        EndCard();
    }

    private static void Export(List<string> settings)
    {
        string text = ProfileTransfer.ExportText(settings);
        string? file = ProfileTransfer.WriteFile(text, out string reason);
        bool copied;
        try
        {
            GUIUtility.systemCopyBuffer = text;
            copied = true;
        }
        catch (Exception)
        {
            copied = false;
        }
        string what = $"{settings.Count} setting{(settings.Count == 1 ? string.Empty : "s")} of {Profiles.Active.Name}";
        if (file == null) Toasts.Show("profiles", copied ? $"Copied {what} to the clipboard; the file wasn't saved: {reason}." : $"Export failed: {reason}.", ToastKind.Warning);
        else Toasts.Show("profiles", $"Exported {what} to exports/{file}{(copied ? " and the clipboard" : string.Empty)}.", ToastKind.Info);
    }

    private static void ImportCard()
    {
        BeginCard("IMPORT");
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        if (s_import == null)
        {
            Hint("Import from the clipboard or an export file, then choose what to take.");
            var sources = new List<string> { "Clipboard" };
            sources.AddRange(ProfileTransfer.Files());
            s_importSource = Math.Clamp(Dropdown.Draw("import.source", sources, Math.Min(s_importSource, sources.Count - 1), GUILayout.ExpandWidth(true), GUILayout.Height(30f)), 0, sources.Count - 1);
            GUILayout.Space(4f);
            GUILayout.BeginHorizontal();
            if (Pressed(GUILayout.Button("Read", button, GUILayout.ExpandWidth(false)), "import.read"))
            {
                string source = sources[s_importSource];
                bool clipboard = s_importSource == 0;
                s_pending = () => ReadImport(clipboard, source);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EndCard();
            return;
        }

        var export = s_import;
        Hint($"{export.Name}, exported {export.Exported}. Choose what to take; open a part to see each value it holds.");
        // Only what the export holds, with its values.
        SettingsTree(ProfileTransfer.Segments.Where(x => export.Segments.Contains(x.Id)).ToList(), s_importChosen, "import", export.ValueOf);
        GUILayout.Space(6f);
        var targets = new List<string> { $"Into {Profiles.Active.Name}", "As a new profile" };
        s_importTarget = Dropdown.Draw("import.target", targets, s_importTarget, GUILayout.ExpandWidth(true), GUILayout.Height(30f));
        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && s_importChosen.Count > 0;
        if (Pressed(GUILayout.Button("Import", button, GUILayout.ExpandWidth(false)), "import.apply"))
        {
            var chosen = s_importChosen.ToList();
            bool asNew = s_importTarget == 1;
            s_pending = () => Import(export, chosen, asNew);
        }
        GUI.enabled = wasEnabled;
        if (Pressed(GUILayout.Button("Cancel", button, GUILayout.ExpandWidth(false)), "import.cancel")) s_import = null;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        EndCard();
    }

    private static void ReadImport(bool clipboard, string file)
    {
        string reason;
        ProfileExport? export;
        if (clipboard)
        {
            string text;
            try
            {
                text = GUIUtility.systemCopyBuffer ?? string.Empty;
            }
            catch (Exception e)
            {
                Toasts.Show("profiles", $"The clipboard couldn't be read: {e.Message}.", ToastKind.Warning);
                return;
            }
            export = ProfileTransfer.Read(text, out reason);
        }
        else
        {
            export = ProfileTransfer.ReadFile(file, out reason);
        }
        if (export == null)
        {
            Toasts.Show("profiles", $"Nothing to import: {reason}.", ToastKind.Warning);
            return;
        }
        if (export.Segments.Count == 0)
        {
            Toasts.Show("profiles", "Nothing to import: the export holds no settings this version knows.", ToastKind.Warning);
            return;
        }
        s_import = export;
        s_importChosen = new HashSet<string>(ProfileTransfer.Segments.Where(x => export.Segments.Contains(x.Id))
            .SelectMany(x => x.Section.Settings).Where(x => export.ValueOf(x) != null).Select(ProfileTransfer.Key));
        s_importTarget = 0;
    }

    private static void Import(ProfileExport export, List<string> settings, bool asNew)
    {
        if (asNew)
        {
            var made = Profiles.Create(ProfilesFeature.NextName(export.Name), copy: false, out string reason);
            if (made == null)
            {
                Toasts.Show("profiles", $"No profile made: {reason}.", ToastKind.Warning);
                return;
            }
            Profiles.Use(made.Id, out _);
        }
        var (set, skipped) = ProfileTransfer.Apply(export, settings);
        s_import = null;
        Toasts.Show("profiles", $"Imported {set} setting{(set == 1 ? string.Empty : "s")} into {Profiles.Active.Name}"
            + (skipped > 0 ? $"; {skipped} didn't fit and were skipped." : "."), skipped > 0 ? ToastKind.Warning : ToastKind.Info);
    }

    // ---- The tree: groups, their parts, each part's settings ----

    // Folded groups and opened parts, by card and id.
    private static readonly HashSet<string> s_collapsed = new();
    private static readonly HashSet<string> s_expanded = new();
    private const float Indent = 18f;

    // Groups fold under their titles; each part is a row whose name opens it (a chevron) and whose switch takes all its
    // settings; an open part lists its settings on tree lines, each with its value and its own switch. value gives the
    // value to show for a setting, or null for one to leave out.
    private static void SettingsTree(IReadOnlyList<ProfileSegment> segments, HashSet<string> chosen, string key, Func<Setting, JsonNode?> value)
    {
        List<Setting> Shown(ProfileSegment segment) => segment.Section.Settings.Where(x => value(x) != null).ToList();
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        if (Pressed(GUILayout.Button("All", button, GUILayout.ExpandWidth(false)), key + ".all"))
            chosen.UnionWith(segments.SelectMany(Shown).Select(ProfileTransfer.Key));
        if (Pressed(GUILayout.Button("None", button, GUILayout.ExpandWidth(false)), key + ".none")) chosen.Clear();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);

        foreach (var group in segments.Select(x => x.Group).Distinct())
        {
            var parts = segments.Where(x => x.Group == group).ToList();
            var all = parts.SelectMany(Shown).Select(ProfileTransfer.Key).ToList();
            GUILayout.Space(6f);
            if (!GroupHeader($"{key}/{group.Id}", group.Name.ToUpperInvariant(), all.Count(chosen.Contains), all.Count)) continue;
            for (int p = 0; p < parts.Count; p++)
            {
                var segment = parts[p];
                bool lastPart = p == parts.Count - 1;
                var settings = Shown(segment);
                var keys = settings.Select(ProfileTransfer.Key).ToList();
                string label = segment.Section == Config.Diagnostics ? "Log level"
                    : segment.Section.Title == group.Name ? $"{group.Name} on/off" : segment.Section.Title;
                string id = $"{key}/{segment.Id}";
                bool open = s_expanded.Contains(id);
                int count = keys.Count(chosen.Contains);
                var (toggleOpen, toggleOn) = PartRow(id, label, count, keys.Count, lastPart);
                if (toggleOpen)
                {
                    if (open) s_expanded.Remove(id);
                    else s_expanded.Add(id);
                }
                if (toggleOn)
                {
                    if (count == keys.Count) chosen.ExceptWith(keys);
                    else chosen.UnionWith(keys);
                }
                if (!open) continue;
                for (int i = 0; i < settings.Count; i++)
                {
                    string settingKey = keys[i];
                    bool on = chosen.Contains(settingKey);
                    JsonNode? stored = value(settings[i]);
                    if (LeafRow(settingKey, settings[i].Label, settings[i].Describe(stored) ?? ValueText(stored), on, lastPart, i == settings.Count - 1))
                    {
                        if (on) chosen.Remove(settingKey);
                        else chosen.Add(settingKey);
                    }
                }
            }
        }
    }

    // Tree lines like a file listing: at each ancestor level a straight line while that ancestor has siblings below,
    // then this row's own branch (├ or, for the last, └). Levels are Branch apart from the row's left edge.
    private const float Branch = 20f;

    private static void Connectors(Rect row, bool[] ancestorsLast, bool last)
    {
        for (int i = 0; i < ancestorsLast.Length; i++)
        {
            if (!ancestorsLast[i]) Line(new Rect(row.x + 6f + i * Branch, row.y, 1f, row.height));
        }
        float x = row.x + 6f + ancestorsLast.Length * Branch;
        Line(new Rect(x, row.y, 1f, last ? row.height / 2f : row.height));
        Line(new Rect(x, row.center.y, 10f, 1f));
    }

    // Where a row's text starts at a tree level.
    private static float TextX(Rect row, int level) => row.x + 6f + level * Branch + 14f;

    // A part on a branch under its group: its name (a click opens its settings), "n of m" while partly chosen, and a
    // switch for all its settings (dimmed while partly on).
    private static (bool ToggleOpen, bool ToggleOn) PartRow(string id, string label, int count, int total, bool last)
    {
        Rect row = GUILayoutUtility.GetRect(10f, 28f, GUILayout.ExpandWidth(true));
        var knob = new Rect(row.xMax - 36f, row.y + (row.height - 20f) / 2f, 36f, 20f);
        float textX = TextX(row, 0);
        GUIStyle style = Style(() => Theme.RowLabel, GUI.skin.label);
        var name = new Rect(textX, row.y, Mathf.Min(style.CalcSize(new GUIContent(label)).x + 8f, knob.x - textX - 76f), row.height);
        bool clickedName = GUI.Button(name, GUIContent.none, GUIStyle.none);
        bool clickedKnob = GUI.Button(new Rect(knob.x - 4f, row.y, knob.width + 4f, row.height), GUIContent.none, GUIStyle.none);
        bool on = count > 0, partial = count > 0 && count < total;
        if (Event.current.type == EventType.Repaint)
        {
            Connectors(row, Array.Empty<bool>(), last);
            Color previous = GUI.contentColor;
            // Brighter under the cursor: the name opens and closes the part.
            if (Themed) GUI.contentColor = name.Contains(Event.current.mousePosition) ? Theme.AccentText : previous;
            Widgets.Paint(name, label, style);
            GUI.contentColor = previous;
            if (partial) Widgets.Paint(new Rect(knob.x - 70f, row.y, 64f, row.height), $"{count} of {total}", ValueStyle());
            if (Themed)
            {
                Color color = GUI.color;
                if (partial) GUI.color = new Color(color.r, color.g, color.b, color.a * 0.5f);
                bool knobHover = knob.Contains(Event.current.mousePosition);
                GUI.DrawTexture(knob, on ? (knobHover ? Theme.SwitchOnHover : Theme.SwitchOn) : (knobHover ? Theme.SwitchOffHover : Theme.SwitchOff));
                GUI.color = color;
            }
        }
        bool toggleOpen = Focus.Item("part." + id, name) || clickedName;
        bool toggleOn = Focus.Item("part.switch." + id, knob) || clickedKnob;
        return (toggleOpen, toggleOn);
    }

    // A setting one level under its part: the part's line runs on while more parts follow, then its own branch, its
    // name, its value and its switch. A click anywhere on the row flips it.
    private static bool LeafRow(string key, string label, string valueText, bool on, bool partLast, bool last)
    {
        Rect row = GUILayoutUtility.GetRect(10f, 24f, GUILayout.ExpandWidth(true));
        var knob = new Rect(row.xMax - 32f, row.y + (row.height - 18f) / 2f, 32f, 18f);
        bool clicked = GUI.Button(row, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            Connectors(row, new[] { partLast }, last);
            GUIStyle valueStyle = ValueStyle();
            float valueWidth = Mathf.Min(valueStyle.CalcSize(new GUIContent(valueText)).x, 110f);
            var valueRect = new Rect(knob.x - 8f - valueWidth, row.y, valueWidth, row.height);
            float textX = TextX(row, 1);
            Widgets.Paint(new Rect(textX, row.y, valueRect.x - textX - 6f, row.height), label, LeafStyle());
            Widgets.Paint(valueRect, Menu.Fit(valueText, valueStyle, 110f), valueStyle);
            if (Themed)
            {
                bool hover = row.Contains(Event.current.mousePosition);
                GUI.DrawTexture(knob, on ? (hover ? Theme.SwitchOnHover : Theme.SwitchOn) : (hover ? Theme.SwitchOffHover : Theme.SwitchOff));
            }
        }
        return Focus.Item("leaf." + key, row) || clicked;
    }

    // A value as the tree shows it: on/off, a name, a number; "none" for an empty one.
    private static string ValueText(JsonNode? node)
    {
        if (node is not JsonValue json) return "?";
        if (json.TryGetValue(out bool flag)) return flag ? "on" : "off";
        if (json.TryGetValue(out string? text)) return text.Length > 0 ? text : "none";
        if (json.TryGetValue(out double number)) return number.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return json.ToJsonString();
    }

    private static void Line(Rect r)
    {
        Color previous = GUI.color;
        Color dim = Themed ? Theme.Dim : Color.gray;
        GUI.color = new Color(dim.r, dim.g, dim.b, previous.a * 0.6f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = previous;
    }

    private static void Chevron(Rect r, bool open, bool hover)
    {
        if (!Themed) return;
        Color previous = GUI.color;
        GUI.color = new Color(Theme.Text.r, Theme.Text.g, Theme.Text.b, previous.a * (hover ? 1f : 0.7f));
        GUI.DrawTexture(r, open ? Theme.Chevron : Theme.ChevronRight);
        GUI.color = previous;
    }

    // A group's title with a chevron (down while open, right while collapsed); a click or Enter folds it. Collapsed, it
    // says how many of its settings are chosen. Returns whether the group is open.
    private static bool GroupHeader(string key, string title, int chosen, int count)
    {
        bool open = !s_collapsed.Contains(key);
        GUIStyle style = HeaderStyle();
        var content = new GUIContent(open ? title : $"{title}   {chosen} of {count}");
        Rect row = GUILayoutUtility.GetRect(content, style, GUILayout.ExpandWidth(true), GUILayout.Height(22f));
        bool clicked = GUI.Button(row, GUIContent.none, GUIStyle.none);
        if (Event.current.type == EventType.Repaint)
        {
            Chevron(new Rect(row.x, row.center.y - 6f, 12f, 12f), open, row.Contains(Event.current.mousePosition));
            style.Draw(new Rect(row.x + Indent, row.y, row.width - Indent, row.height), content, false, false, false, false);
        }
        if (Focus.Item("fold." + key, row) || clicked)
        {
            if (open) s_collapsed.Add(key);
            else s_collapsed.Remove(key);
            open = !open;
        }
        return open;
    }

    private static GUIStyle? s_header, s_headerBase, s_value, s_valueBase, s_leaf, s_leafBase;

    // The card title's look, centred on the row so it lines up with the chevron.
    private static GUIStyle HeaderStyle() => Derived(ref s_header, ref s_headerBase, Style(() => Theme.CardTitle, GUI.skin.label), _ => { });

    // Values: the hint's dim small text, right-aligned and centred on the row.
    private static GUIStyle ValueStyle() => Derived(ref s_value, ref s_valueBase, Style(() => Theme.Hint, GUI.skin.label), style =>
    {
        style.alignment = TextAnchor.MiddleRight;
        style.wordWrap = false;
    });

    // A setting's name under its part: the row text, one size down.
    private static GUIStyle LeafStyle() => Derived(ref s_leaf, ref s_leafBase, Style(() => Theme.RowLabel, GUI.skin.label), style =>
    {
        style.fontSize = Math.Max(10, (Themed ? Theme.Hint.fontSize : style.fontSize) + 1);
        style.fontStyle = FontStyle.Normal;
    });

    // A copy of a theme style, centred vertically with no padding, remade when the theme rebuilds its styles.
    private static GUIStyle Derived(ref GUIStyle? style, ref GUIStyle? from, GUIStyle source, Action<GUIStyle> change)
    {
        if (style == null || !ReferenceEquals(from, source))
        {
            from = source;
            style = new GUIStyle(source) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            change(style);
        }
        return style;
    }

    private static void DrawCard()
    {
        var feature = FeatureHost.Find("profiles");
        bool running = feature == null || feature.State == FeatureState.Running;
        var profiles = Profiles.All;
        var active = Profiles.Active;

        BeginCard("PROFILE");
        if (!running)
        {
            Hint(Menu.Reason(feature!) + " Default is in use.");
            EndCard();
            return;
        }
        Hint(Profiles.HasCharacter ? "This character uses:" : "Out of game, you edit:");
        if (s_renaming) RenameRow(active);
        else PickRow(profiles, active);
        if (s_deleting) DeleteRow(active);
        ButtonRow(active);
        Hint(Profiles.HasCharacter
            ? "Every setting on every page is saved in this profile. Default is the shared one: characters use it until they pick another."
            : Profiles.State == CharacterState.Unreadable
                ? "This character's file can't be read, so its pick isn't saved. Diagnostics says why."
                : "Every setting on every page is saved in this profile. Each character uses its own pick when it enters the game; Default until it picks another.");
        EndCard();
    }

    private static void PickRow(IReadOnlyList<ProfileInfo> profiles, ProfileInfo active)
    {
        var labels = profiles.Select(p => p.Name + Users(p)).ToList();
        int current = profiles.ToList().FindIndex(p => p.Id == active.Id);
        int picked = Dropdown.Draw("profile", labels, current, GUILayout.ExpandWidth(true), GUILayout.Height(30f));
        if (picked == current || picked < 0) return;
        string id = profiles[picked].Id, name = profiles[picked].Name;
        s_deleting = false;
        s_pending = () =>
        {
            if (!Profiles.Use(id, out string reason)) Toasts.Show("profiles", $"Could not switch to {name}: {reason}.", ToastKind.Warning);
            else Toasts.Show("profiles", Profiles.HasCharacter ? $"This character now uses {name}." : $"Editing {name}.", ToastKind.Info);
        };
    }

    // How many characters use a profile, after its name in the list.
    private static string Users(ProfileInfo profile)
    {
        int users = Profiles.Users(profile.Id);
        return users > 0 ? $"   ·  {users} character{(users == 1 ? string.Empty : "s")}" : string.Empty;
    }

    private static void ButtonRow(ProfileInfo active)
    {
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        if (Pressed(GUILayout.Button("New", button, GUILayout.ExpandWidth(false)), "new"))
            s_pending = () => Made(Profiles.Create(ProfilesFeature.NextName("Profile"), copy: false, out string reason), reason, "a copy of Default");
        if (Pressed(GUILayout.Button("Duplicate", button, GUILayout.ExpandWidth(false)), "duplicate"))
            s_pending = () => Made(Profiles.Create(ProfilesFeature.NextName(active.Name + " copy"), copy: true, out string reason), reason, $"a copy of {active.Name}");
        // Default is the shared profile: it can't be renamed or deleted.
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && !active.IsDefault && !s_renaming;
        if (Pressed(GUILayout.Button("Rename", button, GUILayout.ExpandWidth(false)), "rename"))
        {
            s_renaming = true;
            s_deleting = false;
            s_name = active.Name;
        }
        if (Pressed(GUILayout.Button("Delete", button, GUILayout.ExpandWidth(false)), "delete")) s_deleting = true;
        GUI.enabled = wasEnabled;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }

    // A new profile is put in use and goes straight to renaming, so it gets a name of the player's own.
    private static void Made(ProfileInfo? profile, string reason, string what)
    {
        if (profile == null)
        {
            Toasts.Show("profiles", $"No profile made: {reason}.", ToastKind.Warning);
            return;
        }
        Profiles.Use(profile.Id, out _);
        Toasts.Show("profiles", $"{profile.Name} made as {what}.", ToastKind.Info);
        s_renaming = true;
        s_deleting = false;
        s_name = profile.Name;
    }

    private static void RenameRow(ProfileInfo active)
    {
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        bool focused = GUI.GetNameOfFocusedControl() == NameControl;
        Event e = Event.current;
        bool enter = focused && e.type == EventType.KeyDown && e.keyCode is KeyCode.Return or KeyCode.KeypadEnter;
        if (enter) e.Use();

        GUILayout.BeginHorizontal(GUILayout.Height(30f));
        GUI.SetNextControlName(NameControl);
        s_name = GUILayout.TextField(s_name, Style(() => Theme.SearchField, GUI.skin.textField), GUILayout.ExpandWidth(true));
        Rect field = GUILayoutUtility.GetLastRect();
        if (focused) Focus.Take("name");
        if (Focus.Item("name", field) || Focus.Arrived("name")) GUI.FocusControl(NameControl);
        GUILayout.Space(8f);
        bool save = Pressed(GUILayout.Button("Save", button, GUILayout.ExpandWidth(false)), "rename.save") || enter;
        bool cancel = Pressed(GUILayout.Button("Cancel", button, GUILayout.ExpandWidth(false)), "rename.cancel");
        GUILayout.EndHorizontal();

        if (cancel)
        {
            s_renaming = false;
            GUIUtility.keyboardControl = 0;
        }
        else if (save)
        {
            string id = active.Id, name = s_name;
            s_pending = () =>
            {
                if (Profiles.Rename(id, name, out string reason))
                {
                    s_renaming = false;
                    GUIUtility.keyboardControl = 0;
                }
                else
                {
                    Toasts.Show("profiles", $"Not renamed: {reason}.", ToastKind.Warning);
                }
            };
        }
    }

    private static void DeleteRow(ProfileInfo active)
    {
        GUIStyle button = Style(() => Theme.SmallButton, GUI.skin.button);
        int users = Profiles.Users(active.Id);
        GUILayout.Space(4f);
        Hint(Colored($"Delete {active.Name}?", Theme.Pause)
            + $" {(users > 0 ? $"{users} character{(users == 1 ? " goes" : "s go")} back to Default." : "No character uses it.")}");
        GUILayout.BeginHorizontal();
        if (Pressed(GUILayout.Button("Delete", button, GUILayout.ExpandWidth(false)), "delete.confirm"))
        {
            string id = active.Id, name = active.Name;
            s_pending = () =>
            {
                s_deleting = false;
                if (Profiles.Delete(id, out string reason)) Toasts.Show("profiles", $"{name} deleted.", ToastKind.Info);
                else Toasts.Show("profiles", $"{name} not deleted: {reason}.", ToastKind.Warning);
            };
        }
        if (Pressed(GUILayout.Button("Keep", button, GUILayout.ExpandWidth(false)), "delete.keep")) s_deleting = false;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
    }
}
