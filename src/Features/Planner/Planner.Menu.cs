using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

// The Planner tab as data: the library layout (builds on the left, as the Macros tab lists macros) with the paste in
// its toolbar and the last attempt's outcome under it, the build view that opens in the library's place, and the
// Import and Settings pages its buttons open. The menu draws it.
internal static partial class Planner
{
    // Declared before the view, which lists the paste button in its toolbar.
    private static readonly ActionButton s_pasteButton = TabButton("Import", "paste", PasteFieldOrClipboard,
        tip: "Imports the planner link or export in the field, or the one on the clipboard when the field is empty");
    private static readonly ActionButton s_dataLinkButton = TabButton("Open build data link", "datalink", OpenDataLink,
        tip: "Opens this build's data in the browser; paste the page here");
    private static readonly ActionButton s_replaceButton = TabButton("Replace", "replace", ReplacePending, tip: "Replaces the build in the library with this import");
    private static readonly ActionButton s_keepButton = TabButton("Keep", "keep", KeepPending, tip: "Keeps the build in the library and drops this import");

    public static readonly LibraryView View = new()
    {
        Lines = LibraryLines,
        EmptyText = "No builds yet.",
        NoSelectionText = "Select a build.",
        Open = line => s_viewing = line.Key,
        Actions = BuildActions,
        ActionHint = BuildHint,
        Rename = (line, name) => Library.Rename(line.Key, name),
        Editor = BuildEditor,
        Back = () =>
        {
            if (s_viewing == null) return false;
            s_viewing = null;
            return true;
        },
        // Polled here too, so a request started before the feature runs (no character yet) still lands.
        Banner = () =>
        {
            PollFetch();
            SyncToolbar();
            return s_outcome;
        },
        Toolbar = { s_pasteButton },
        Field = new ToolbarField("planner.link", () => s_link ?? string.Empty, text => s_link = text, "Planner link or export", LinkMaxLength)
        {
            Submit = PasteFieldOrClipboard,
        },
        Footer = { S.Enabled },
        // To the planner's settings in the Config tree, not a page in this tab.
        FooterButtons = { new ActionButton("Settings", "menu.config.planner") { Tip = "Opens Config › Planner" } },
    };

    public static readonly MenuEntry Entry = new(MenuCategory.Planner, order: 10)
    {
        Title = "Library",
        Layout = MenuLayout.Library,
        Library = View,
        Pages = { new MenuPage("Library") { Resettable = false } },
    };

    /// <summary>The settings in the Config tree: Overlay first, then a sub-tab per other section.</summary>
    public static readonly MenuEntry ConfigEntry = ConfigSettings();

    private static ActionButton TabButton(string label, string key, Action click, Func<bool>? enabled = null, string? tip = null, bool primary = false,
        Func<bool>? visible = null) =>
        new(label, "planner." + key, primary) { OnClick = click, EnabledWhen = enabled, VisibleWhen = visible, Tip = tip };

    private static bool s_toolbarChanged;

    // A toolbar button runs while the menu walks the toolbar, so the list changes later, from the banner.
    private static void ImportChanged() => s_toolbarChanged = true;

    // The toolbar offers the outcome's follow-ups only while they apply.
    private static void SyncToolbar()
    {
        if (!s_toolbarChanged) return;
        s_toolbarChanged = false;
        var bar = View.Toolbar;
        bar.Clear();
        bar.Add(s_pasteButton);
        if (BuildDataLink != null) bar.Add(s_dataLinkButton);
        if (s_pending != null)
        {
            bar.Add(s_replaceButton);
            bar.Add(s_keepButton);
        }
    }

    private static void OpenDataLink()
    {
        if (BuildDataLink is { } url) UnityEngine.Application.OpenURL(url);
    }

    // ---- Import field ----

    // Room for a pasted export as well as a link; a longer paste is refused by the import itself.
    private const int LinkMaxLength = 1 << 20;
    private static string s_link = string.Empty;

    private static void PasteFieldOrClipboard()
    {
        string text = s_link.Trim().Length > 0 ? s_link : Clipboard.Text;
        s_link = string.Empty;
        Paste(text);
    }

    // ---- Library ----

    private static readonly List<LibraryLine> s_lines = new();
    private static (int Generation, string? Bound) s_linesFor = (-1, null);

    // Rebuilt only when the library or the character's build changes.
    private static IReadOnlyList<LibraryLine> LibraryLines()
    {
        Library.EnsureLoaded();
        string? bound = Library.Binding(Profiles.Key)?.Build;
        if (s_linesFor == (Library.Generation, bound)) return s_lines;
        s_linesFor = (Library.Generation, bound);
        s_lines.Clear();
        foreach (var build in Library.Builds)
        {
            string state = $"{SourceName(build)} · {StageCount(build)}";
            bool active = build.Id == bound;
            s_lines.Add(new LibraryLine(build.Id, 0, build.Name, active ? "in use · " + state : state, active ? StatusState.Active : StatusState.Off, null, false));
        }
        return s_lines;
    }

    private static string SourceName(Build build) => build.Source switch
    {
        MaxrollFormat.Source => "Maxroll",
        MaxrollFormat.ExportSource => "Maxroll export",
        LetFormat.Source => "Last Epoch Tools",
        _ => build.Source,
    };

    private static string StageCount(Build build) => build.Stages.Count == 1 ? "1 stage" : $"{build.Stages.Count} stages";

    /// <summary>"From Maxroll by &lt;author&gt;", "From Last Epoch Tools" or "Maxroll export".</summary>
    public static string Credit(Build build) => build.Source switch
    {
        MaxrollFormat.ExportSource => "Maxroll export",
        _ when build.Author is { Length: > 0 } author => $"From {SourceName(build)} by {author}",
        _ => $"From {SourceName(build)}",
    };

    private static string? s_actionsFor;
    private static IReadOnlyList<ActionButton> s_actions = Array.Empty<ActionButton>();

    private static IReadOnlyList<ActionButton> BuildActions(LibraryLine? line)
    {
        if (line == null) return Array.Empty<ActionButton>();
        if (s_actionsFor == line.Key) return s_actions;
        string id = line.Key;
        s_actionsFor = id;
        s_actions = new[]
        {
            TabButton("Open", "open", () => s_viewing = id),
            TabButton("Rename", "rename", () =>
            {
                if (View.Line(id) is { } renamed) View.BeginRename(renamed);
            }),
            TabButton("Delete", "delete", () => Delete(id), tip: "Removes the build from the library and from every character that used it"),
        };
        return s_actions;
    }

    private static void Delete(string id)
    {
        if (!Library.Delete(id)) return;
        if (s_viewing == id) s_viewing = null;
    }

    private static (string? Id, int Generation) s_hintFor = (null, -1);
    private static string? s_hint;

    private static string? BuildHint(LibraryLine? line)
    {
        if (line == null) return null;
        if (s_hintFor == (line.Key, Library.Generation)) return s_hint;
        s_hintFor = (line.Key, Library.Generation);
        s_hint = Library.Find(line.Key) is { } build ? $"{Credit(build)} · {StageCount(build)}" : null;
        return s_hint;
    }

    // ---- Build view ----

    private static string? s_viewing;
    private static LibraryEditor? s_editor;
    private static int s_editorGeneration = -1;

    // Made again when the library changes (a rename, a replaced build).
    private static LibraryEditor? BuildEditor()
    {
        if (s_viewing == null) return null;
        Library.EnsureLoaded();
        if (s_editor != null && s_editor.Key == s_viewing && s_editorGeneration == Library.Generation) return s_editor;
        if (Library.Find(s_viewing) is not { } build)
        {
            s_viewing = null;
            s_editor = null;
            return null;
        }
        s_editorGeneration = Library.Generation;
        s_editor = MakeBuildEditor(build);
        return s_editor;
    }

    private static LibraryEditor MakeBuildEditor(Build build)
    {
        string id = build.Id, credit = Credit(build);
        var rows = new List<MenuRow>
        {
            new HintRow(() => Words.NoCharacter) { Key = "nocharacter", VisibleWhen = () => Profiles.Key == null },
            new HintRow(() => "This character uses this build.") { Key = "inuse", VisibleWhen = () => Library.Binding(Profiles.Key)?.Build == id },
        };
        rows.Add(new HintRow(() => s_run?.MasteryWords ?? string.Empty)
        {
            Key = "mastery",
            VisibleWhen = () => s_run is { Guidance.DifferentMastery: true, MasteryWords: not null } run && run.Build?.Id == id,
        });
        if (build.Stages.Exists(stage => stage.SlotsInferred)) rows.Add(new HintRow(() => Words.InferredSlots) { Key = "inferred" });
        rows.Add(new HeadingRow("Not in this game version") { VisibleWhen = () => Unknown(build) });
        rows.Add(new HintRow(() => s_unknownSummary ?? string.Empty) { Key = "unknown", VisibleWhen = () => Unknown(build) });
        rows.Add(new HintRow(() => s_unknownList ?? string.Empty) { Key = "unknown.list", VisibleWhen = () => Unknown(build) });
        rows.Add(new HeadingRow("Specialisations") { VisibleWhen = () => Specialisations(id) });
        rows.Add(new HintRow(() => s_specMissing ?? string.Empty) { Key = "spec.missing", VisibleWhen = () => Specialisations(id) && s_specMissing != null });
        rows.Add(new HintRow(() => s_specExtra ?? string.Empty) { Key = "spec.notinbuild", VisibleWhen = () => Specialisations(id) && s_specExtra != null });

        var editor = new LibraryEditor(id, () => build.Name, () => rows) { Note = () => credit };
        if (build.Url is { } url)
            editor.Buttons.Add(TabButton("Open planner page", "page", () => UnityEngine.Application.OpenURL(url), tip: "Opens the build's page in the browser"));
        editor.Buttons.Add(TabButton("Use for this character", "use", () =>
        {
            if (Profiles.Key is { } character) Library.Bind(character, id);
        }, () => Profiles.Key is { } character && Library.Binding(character)?.Build != id, "Guides the character in play with this build, from its first stage"));
        editor.Buttons.Add(TabButton("Close", "close", () => s_viewing = null));
        return editor;
    }

    // The ids this game version lacks, for the build shown. The check needs the game's data, so until it can be read
    // it's tried again once a second.
    private static (string? Id, int Generation, float Retry) s_unknownFor = (null, -1, 0f);
    private static string? s_unknownSummary, s_unknownList;

    private static bool Unknown(Build build)
    {
        if (s_unknownFor.Id != build.Id || s_unknownFor.Generation != Library.Generation || Clock.Now >= s_unknownFor.Retry)
        {
            var resolution = Resolve(build);
            s_unknownFor = (build.Id, Library.Generation, resolution.Unread ? Clock.Now + 1f : float.MaxValue);
            bool any = !resolution.Unread && resolution.UnknownNodes.Count + resolution.UnknownSkills.Count > 0;
            s_unknownSummary = any ? Words.UnknownIds(resolution) : null;
            s_unknownList = any ? UnknownList(resolution) : null;
        }
        return s_unknownSummary != null;
    }

    // The planned skills not specialised and the specialised skills the stage doesn't plan, for the character's build;
    // made again only when the guidance changes.
    private static Guidance? s_specFor;
    private static string? s_specMissing, s_specExtra;

    private static bool Specialisations(string id)
    {
        if (s_run is not { Guidance: { } guidance } run || run.Build?.Id != id) return false;
        if (s_specFor != guidance)
        {
            s_specFor = guidance;
            s_specMissing = SkillList("Not specialised: ", run.Feature, guidance, TreeFlags.SkillMissing);
            s_specExtra = SkillList("Specialised, not in this build: ", run.Feature, guidance, TreeFlags.SkillNotInBuild);
        }
        return s_specMissing != null || s_specExtra != null;
    }

    private static string? SkillList(string lead, Core.Feature feature, Guidance guidance, TreeFlags flag)
    {
        List<string>? names = null;
        foreach (var skill in guidance.Skills)
            if ((skill.Tree.Flags & flag) != 0) (names ??= new List<string>()).Add(SkillName(feature, skill));
        return names != null ? lead + string.Join(", ", names) + "." : null;
    }

    private static string UnknownList(BuildResolution resolution)
    {
        var parts = new List<string>();
        if (resolution.UnknownSkills.Count > 0) parts.Add("Skills: " + string.Join(", ", resolution.UnknownSkills) + ".");
        if (resolution.UnknownNodes.Count > 0)
            parts.Add("Nodes: " + string.Join(", ", resolution.UnknownNodes.Select(n => $"{(n.TreeId.Length > 0 ? n.TreeId : "?")} {n.Node}")) + ".");
        return string.Join(" ", parts);
    }

    // ---- Settings in the Config tree ----

    // Each section a sub-tab in its declared order; search and resets come from the settings.
    private static MenuEntry ConfigSettings()
    {
        // No status row: the page title's tag already shows the state and why.
        var entry = new MenuEntry(MenuCategory.Combat, order: 35) { Title = "Planner" };
        // The master switch and the outline settings share the first sub-tab, Overlay; every other section has its own.
        var overlay = new MenuPage(S.Overlay.Title);
        entry.Pages.Add(overlay);
        foreach (var section in S.Group.Sections)
        {
            bool first = section == S.Main || section == S.Overlay;
            var page = first ? overlay : new MenuPage(section.Title);
            foreach (var setting in section.Settings)
                page.Rows.Add(new SettingRow(setting) { EnabledWhen = EnabledWhen(setting) });
            if (!first) entry.Pages.Add(page);
        }
        return entry;
    }

    // A row that only shapes another setting's output is greyed while that setting is off.
    private static Func<bool>? EnabledWhen(Setting setting)
    {
        if (setting == S.NextCount) return () => S.NextSteps.Value || S.HighlightNext.Value;
        if (setting == S.FillOpacity) return () => S.FillTint.Value;
        if (setting == S.NextLabelSize) return () => S.NextLabel.Value;
        if (setting == S.NextColour) return () => S.NextOwnColour.Value;
        return null;
    }
}
