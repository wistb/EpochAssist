using System.Text.Json.Nodes;
using EpochAssist.Core;
using UnityEngine;
using static EpochAssist.Ui.Widgets;

namespace EpochAssist.Ui;

/// <summary>
/// Config › General › Profiles, the menu's profiles block: the profile in use (the playing character's in game, the one
/// being edited out of it) with New, Duplicate, Rename and Delete; export as a checkbox tree of every setting; import
/// from the clipboard or an export file, into the profile in use or a new one. Every setting on every page belongs to the
/// profile in use.
/// </summary>
internal static class ProfilesPage
{
    private const string NameControl = "EpochAssistProfileName";

    private static bool s_renaming;
    private static string s_name = string.Empty;
    private static bool s_deleting;
    // Changes run at the start of a layout pass, so every pass of a frame draws the same thing.
    private static Action? s_pending;

    public const string Block = "profiles";

    // Chosen settings by key (group/section/setting); every one to start with.
    private static HashSet<string>? s_exportChosen;
    private static HashSet<string> s_importChosen = new();
    private static ProfileExport? s_import;
    private static int s_importSource, s_importTarget;
    private static CheckTree? s_exportTree, s_importTree;

    public static void Register() => MenuBlocks.Register(Block, (rect, _) => Draw(rect), _ => BridgeLines());

    public static void Reset()
    {
        s_renaming = s_deleting = false;
        s_pending = null;
        s_import = null;
        s_exportChosen = null;
        s_exportTree = s_importTree = null;
    }

    private static IEnumerable<string> BridgeLines()
    {
        yield return $"profile {RowText.Quote(Profiles.Active.Name)} profiles={Profiles.All.Count}";
        yield return $"export chosen={s_exportChosen?.Count ?? 0}";
        yield return s_import == null ? "import none" : $"import {RowText.Quote(s_import.Name)} chosen={s_importChosen.Count}";
    }

    private static float LabelWidth => RowLayout.LabelWidth(PageView.BlockWidth);

    public static void Draw(Rect rect)
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
        ProfileRows();
        ExportRows();
        ImportRows();
        Menu.EndScroll();
        Popups.PopOrigin();
        GUILayout.EndArea();
    }

    // A row with a label in the label column and the rest of the width for its controls.
    private static Rect Row(string label, float height = RowLayout.ControlHeight)
    {
        Rect row = GUILayoutUtility.GetRect(10f, height + RowLayout.RowGap, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint && label.Length > 0)
            Widgets.Paint(new Rect(row.x, row.y, LabelWidth, RowLayout.ControlHeight), label, Style(() => Theme.Label, GUI.skin.label));
        return new Rect(row.x + LabelWidth + RowLayout.Gap, row.y, row.width - LabelWidth - RowLayout.Gap, height);
    }

    // Buttons left to right from x; returns the index pressed, or -1. Disabled ones (enabled false) take no press.
    private static int Buttons(Rect row, string keyPrefix, params (string Label, bool Enabled, bool Primary)[] buttons)
    {
        float x = row.x;
        int pressed = -1;
        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, enabled, primary) = buttons[i];
            float w = Controls.ButtonWidth(label);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && enabled;
            if (Controls.Button(new Rect(x, row.y, w, RowLayout.ControlHeight), label, keyPrefix + label.ToLowerInvariant(), primary)) pressed = i;
            GUI.enabled = wasEnabled;
            x += w + 6f;
        }
        return pressed;
    }

    /// <summary>A small-caps heading with a divider, for blocks laid out with GUILayout (also the Diagnostics pages).</summary>
    internal static void Heading(string text)
    {
        Rect r = GUILayoutUtility.GetRect(10f, 26f, GUILayout.ExpandWidth(true));
        if (Event.current.type != EventType.Repaint) return;
        GUIStyle style = Style(() => Theme.Heading, GUI.skin.label);
        string upper = text.ToUpperInvariant();
        var line = new Rect(r.x, r.y + 8f, r.width, 14f);
        Widgets.Paint(line, upper, style);
        float x = line.x + TextCache.Width(upper, style) + 8f;
        if (Themed) Theme.Fill(new Rect(x, Mathf.Round(line.center.y), line.xMax - x, 1f), Theme.DividerColor);
    }

    private static void ProfileRows()
    {
        var feature = FeatureHost.Find("profiles");
        if (feature != null && feature.State != FeatureState.Running)
        {
            Hint(Menu.Reason(feature) + " Default is in use.");
            return;
        }
        var profiles = Profiles.All;
        var active = Profiles.Active;
        Rect pick = Row(Profiles.HasCharacter ? "This character uses" : "Out of game, you edit");
        if (s_renaming) RenameRow(pick, active);
        else PickRow(pick, profiles, active);

        Rect buttons = Row(string.Empty);
        // Default is the shared profile: it can't be renamed or deleted.
        bool own = !active.IsDefault && !s_renaming;
        switch (Buttons(buttons, "profile.", ("New", true, false), ("Duplicate", true, false), ("Rename", own, false), ("Delete", own, false)))
        {
            case 0:
                s_pending = () => Made(Profiles.Create(ProfilesFeature.NextName("Profile"), copy: false, out string reason), reason, "a copy of Default");
                break;
            case 1:
                s_pending = () => Made(Profiles.Create(ProfilesFeature.NextName(active.Name + " copy"), copy: true, out string reason), reason, $"a copy of {active.Name}");
                break;
            case 2:
                s_renaming = true;
                s_deleting = false;
                s_name = active.Name;
                break;
            case 3:
                s_deleting = true;
                break;
        }
        if (s_deleting) DeleteRows(active);
        Hint(Profiles.HasCharacter
            ? "Every setting on every page is saved in this profile. Default is the shared one: characters use it until they pick another."
            : Profiles.State == CharacterState.Unreadable
                ? "This character's file can't be read, so its pick isn't saved. Diagnostics says why."
                : "Every setting on every page is saved in this profile. Each character uses its own pick when it enters the game; Default until it picks another.");
    }

    private static void PickRow(Rect row, IReadOnlyList<ProfileInfo> profiles, ProfileInfo active)
    {
        var labels = profiles.Select(p => p.Name + Users(p)).ToList();
        int current = profiles.ToList().FindIndex(p => p.Id == active.Id);
        Dropdown.Draw(new Rect(row.x, row.y, Mathf.Min(220f, row.width), row.height), "profiles.pick", labels, current, picked =>
        {
            if (picked < 0 || picked >= profiles.Count) return;
            string id = profiles[picked].Id, name = profiles[picked].Name;
            s_deleting = false;
            s_pending = () =>
            {
                if (!Profiles.Use(id, out string reason)) Toasts.Show("profiles", $"Could not switch to {name}: {reason}.", ToastKind.Warning);
                else Toasts.Show("profiles", Profiles.HasCharacter ? $"This character now uses {name}." : $"Editing {name}.", ToastKind.Info);
            };
        });
    }

    private static void RenameRow(Rect row, ProfileInfo active)
    {
        bool focused = GUI.GetNameOfFocusedControl() == NameControl;
        Event e = Event.current;
        bool enter = focused && e.type == EventType.KeyDown && e.keyCode is KeyCode.Return or KeyCode.KeypadEnter;
        if (enter) e.Use();
        var field = new Rect(row.x, row.y, Mathf.Min(220f, row.width), row.height);
        GUI.SetNextControlName(NameControl);
        s_name = GUI.TextField(field, s_name, 40, Style(() => Theme.TreeFilter, GUI.skin.textField));
        if (focused) Focus.Take("name");
        if (Focus.Item("name", field) || Focus.Arrived("name")) GUI.FocusControl(NameControl);
        int pressed = Buttons(new Rect(field.xMax + 8f, row.y, row.width, row.height), "rename.", ("Save", true, true), ("Cancel", true, false));
        if (pressed == 1)
        {
            s_renaming = false;
            GUIUtility.keyboardControl = 0;
        }
        else if (pressed == 0 || enter)
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

    private static void DeleteRows(ProfileInfo active)
    {
        int users = Profiles.Users(active.Id);
        Hint(Colored($"Delete {active.Name}?", Theme.Pause)
            + $" {(users > 0 ? $"{users} character{(users == 1 ? " goes" : "s go")} back to Default." : "No character uses it.")}");
        switch (Buttons(Row(string.Empty), "delete.", ("Delete", true, true), ("Keep", true, false)))
        {
            case 0:
                string id = active.Id, name = active.Name;
                s_pending = () =>
                {
                    s_deleting = false;
                    if (Profiles.Delete(id, out string reason)) Toasts.Show("profiles", $"{name} deleted.", ToastKind.Info);
                    else Toasts.Show("profiles", $"{name} not deleted: {reason}.", ToastKind.Warning);
                };
                break;
            case 1:
                s_deleting = false;
                break;
        }
    }

    private static void Hint(string text)
    {
        GUIStyle style = Style(() => Theme.Hint, GUI.skin.label);
        float width = PageView.BlockWidth;
        Rect r = GUILayoutUtility.GetRect(width, Controls.HintHeight(text, width) + 4f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint) Widgets.Paint(r, text, style);
    }

    // The settings of some segments as a checkbox tree: groups, their parts, each part's settings with a value.
    private static IReadOnlyList<CheckNode> TreeOf(IReadOnlyList<ProfileSegment> segments, Func<Setting, JsonNode?> value)
    {
        var groups = new List<CheckNode>();
        foreach (var group in segments.Select(x => x.Group).Distinct())
        {
            var parts = new List<CheckNode>();
            foreach (var segment in segments.Where(x => x.Group == group))
            {
                var leaves = segment.Section.Settings.Select(s => (Setting: s, Value: value(s))).Where(x => x.Value != null)
                    .Select(x => new CheckNode(ProfileTransfer.Key(x.Setting), x.Setting.Label, Array.Empty<CheckNode>(), x.Setting.Describe(x.Value) ?? ValueText(x.Value))).ToList();
                if (leaves.Count == 0) continue;
                string label = segment.Section == Config.Diagnostics ? "Log level" : segment.Section.Title == group.Name ? $"{group.Name} on/off" : segment.Section.Title;
                parts.Add(new CheckNode("s:" + segment.Id, label, leaves));
            }
            if (parts.Count > 0) groups.Add(new CheckNode("g:" + group.Id, group.Name, parts));
        }
        return groups;
    }

    private static void DrawTree(CheckTree tree)
    {
        if (RowKinds.For(tree) is not { } renderer) return;
        var ctx = new RowContext { Path = MenuRoutes.Path };
        float width = PageView.BlockWidth;
        float height = renderer.Height(tree, width, ctx);
        Rect r = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(true));
        renderer.Draw(tree, r, ctx);
    }

    private static void ExportRows()
    {
        var segments = ProfileTransfer.Segments;
        s_exportChosen ??= new HashSet<string>(ProfileTransfer.KeysOf(segments.Select(x => x.Id)));
        s_exportTree ??= new CheckTree(() => TreeOf(ProfileTransfer.Segments, s => s.ToJson(global: false)), s_exportChosen, "export");
        Heading($"Export from {Profiles.Active.Name}");
        Rect row = GUILayoutUtility.GetRect(10f, RowLayout.ControlHeight + RowLayout.RowGap, GUILayout.ExpandWidth(true));
        switch (Buttons(row, "export.", ("All", true, false), ("None", true, false)))
        {
            case 0:
                s_exportChosen.UnionWith(ProfileTransfer.KeysOf(segments.Select(x => x.Id)));
                break;
            case 1:
                s_exportChosen.Clear();
                break;
        }
        float w = Controls.ButtonWidth("Export");
        bool wasEnabled = GUI.enabled;
        GUI.enabled = wasEnabled && s_exportChosen.Count > 0;
        if (Controls.Button(new Rect(row.xMax - w, row.y, w, RowLayout.ControlHeight), "Export", "export", primary: true,
            tip: "Saves to UserData/EpochAssist/exports and copies to the clipboard"))
        {
            var chosen = s_exportChosen.ToList();
            s_pending = () => Export(chosen);
        }
        GUI.enabled = wasEnabled;
        DrawTree(s_exportTree);
    }

    private static void ImportRows()
    {
        Heading("Import");
        if (s_import == null)
        {
            var sources = new List<string> { "Clipboard" };
            sources.AddRange(ProfileTransfer.Files());
            s_importSource = Math.Clamp(s_importSource, 0, sources.Count - 1);
            Rect from = Row("From");
            Dropdown.Draw(new Rect(from.x, from.y, Mathf.Min(220f, from.width), from.height), "import.source", sources, s_importSource, i => s_importSource = i);
            Rect into = Row("Into");
            var targets = new List<string> { $"{Profiles.Active.Name} (in use)", "As a new profile" };
            Dropdown.Draw(new Rect(into.x, into.y, Mathf.Min(220f, into.width), into.height), "import.target", targets, s_importTarget, i => s_importTarget = i);
            if (Buttons(new Rect(into.x + Mathf.Min(220f, into.width) + 8f, into.y, 200f, into.height), "import.", ("Read", true, false)) == 0)
            {
                string source = sources[s_importSource];
                bool clipboard = s_importSource == 0;
                int target = s_importTarget;
                s_pending = () =>
                {
                    ReadImport(clipboard, source);
                    s_importTarget = target;
                };
            }
            Hint("Read takes an export from the clipboard or UserData/EpochAssist/exports, then you choose what to take.");
            return;
        }

        var export = s_import;
        Hint($"{export.Name}, exported {export.Exported}. Choose what to take; open a part to see each value it holds.");
        if (s_importTree == null || !ReferenceEquals(s_importTree.Chosen, s_importChosen))
            s_importTree = new CheckTree(() => TreeOf(ProfileTransfer.Segments.Where(x => export.Segments.Contains(x.Id)).ToList(), export.ValueOf), s_importChosen, "import");
        DrawTree(s_importTree);
        Rect row = Row(s_importTarget == 1 ? "Into a new profile" : $"Into {Profiles.Active.Name}");
        switch (Buttons(row, "import.", ("Import", s_importChosen.Count > 0, true), ("Cancel", true, false)))
        {
            case 0:
                var chosen = s_importChosen.ToList();
                bool asNew = s_importTarget == 1;
                s_pending = () => Import(export, chosen, asNew);
                break;
            case 1:
                s_import = null;
                s_importTree = null;
                break;
        }
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
        // Groups from an older version were updated on the way in; the player is told which.
        string migrated = (export.Migrated.Count > 0 ? $" Updated from an older version: {string.Join(", ", export.MigratedNames)}." : string.Empty)
            + (export.Unreadable.Count > 0 ? $" Kept conditions made by a newer version: {string.Join(", ", export.Unreadable)}." : string.Empty);
        Toasts.Show("profiles", $"Imported {set} setting{(set == 1 ? string.Empty : "s")} into {Profiles.Active.Name}"
            + (skipped > 0 ? $"; {skipped} didn't fit and were skipped." : ".") + migrated, skipped > 0 ? ToastKind.Warning : ToastKind.Info);
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

    // How many characters use a profile, after its name in the list.
    private static string Users(ProfileInfo profile)
    {
        int users = Profiles.Users(profile.Id);
        return users > 0 ? $"   ·  {users} character{(users == 1 ? string.Empty : "s")}" : string.Empty;
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
}
