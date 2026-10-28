using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using UnityEngine;
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
            return s_outcome ?? (Profiles.Key == null ? Words.NoCharacter : null);
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
            // The banner says it too, unless an import outcome holds it.
            new HintRow(() => Words.NoCharacter) { Key = "nocharacter", VisibleWhen = () => Profiles.Key == null && s_outcome != null },
            new HintRow(() => "This character uses this build.") { Key = "inuse", VisibleWhen = () => Library.Binding(Profiles.Key)?.Build == id },
            new TextButtonRow("Stage", () => StageText(build), TabButton("▶", "stage.next", () => StepStage(id, 1), () => CanStep(build, 1), "Next stage"))
            {
                Key = "stage",
                Before = TabButton("◀", "stage.previous", () => StepStage(id, -1), () => CanStep(build, -1), "Previous stage"),
                VisibleWhen = () => Library.Binding(Profiles.Key)?.Build == id,
            },
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
        rows.Add(new HeadingRow("Skill bar") { VisibleWhen = () => BarShown(id) });
        for (int i = 0; i < Stage.BarSlots; i++)
        {
            int slot = i;
            rows.Add(new LiveText($"Slot {slot + 1}", () => (BarText(slot), null)) { Key = $"bar.{slot}", VisibleWhen = () => BarShown(id) });
        }

        var editor = new LibraryEditor(id, () => build.Name, () => GuideRows(id, rows)) { Note = () => credit };
        if (build.Url is { } url)
            editor.Buttons.Add(TabButton("Open planner page", "page", () => UnityEngine.Application.OpenURL(url), tip: "Opens the build's page in the browser"));
        editor.Buttons.Add(TabButton("Use for this character", "use", () =>
        {
            if (Profiles.Key is { } character) Library.Bind(character, id);
        }, () => Profiles.Key is { } character && Library.Binding(character)?.Build != id, "Guides the character in play with this build, from its first stage"));
        editor.Buttons.Add(TabButton("Close", "close", () => s_viewing = null));
        return editor;
    }

    // ---- Stage ----

    // The stage changes only by these buttons, never by level.
    private static (Build? Build, int Stage, int Generation) s_stageFor = (null, -1, -1);
    private static string s_stageText = string.Empty;

    private static string StageText(Build build)
    {
        int stage = Library.Binding(Profiles.Key)?.Stage ?? 0;
        if (s_stageFor == (build, stage, Library.Generation)) return s_stageText;
        s_stageFor = (build, stage, Library.Generation);
        string name = build.Stages[Math.Clamp(stage, 0, build.Stages.Count - 1)].Name.Trim();
        s_stageText = $"{stage + 1} of {build.Stages.Count}" + (name.Length > 0 ? $" · {name}" : string.Empty);
        return s_stageText;
    }

    private static bool CanStep(Build build, int by) =>
        Library.Binding(Profiles.Key) is { } binding && binding.Build == build.Id && binding.Stage + by >= 0 && binding.Stage + by < build.Stages.Count;

    private static void StepStage(string id, int by)
    {
        if (Profiles.Key is { } character && Library.Binding(character) is { } binding && binding.Build == id) Library.SetStage(character, binding.Stage + by);
    }

    // ---- Checklist and next steps ----

    // The build view's rows with the checklist and next steps after them, for the character's build; made again only
    // when the guidance changes, with every text made then.
    private static (List<MenuRow>? Rows, Guidance? Guidance, GearGuidance? Gear) s_guideFor;
    private static List<MenuRow> s_guideRows = new();

    private static IReadOnlyList<MenuRow> GuideRows(string id, List<MenuRow> rows)
    {
        var run = s_run;
        var guidance = run is { Guidance: { } g, Build: { } b } && b.Id == id ? g : null;
        var gear = guidance != null ? run!.Gear : null;
        if (s_guideFor.Rows == rows && s_guideFor.Guidance == guidance && s_guideFor.Gear == gear) return s_guideRows;
        s_guideFor = (rows, guidance, gear);
        s_guideRows = new List<MenuRow>(rows);
        if (guidance != null) AddGuide(s_guideRows, run!, guidance);
        return s_guideRows;
    }

    private static void AddGuide(List<MenuRow> rows, Run run, Guidance guidance)
    {
        var feature = run.Feature;
        var stage = run.Build!.Stages[run.Stage];
        var have = run.Allocation.Bar;
        var planned = guidance.Skills.Where(skill => skill.State != GuidanceState.NotInBuild).ToList();
        // The game data names a skill's nodes after the skill ("Flay Chill Chance"); under the skill's own heading the
        // skill's name is left off.
        Func<int, string> Nodes(string treeId, string skill = "") => node =>
        {
            string name = NodeName(feature, treeId, (byte)node);
            return skill.Length > 0 && name.Length > skill.Length + 1 && name.StartsWith(skill + " ", StringComparison.OrdinalIgnoreCase) ? name[(skill.Length + 1)..] : name;
        };

        rows.Add(new HeadingRow("Checklist"));
        AddSection(rows, "passive", "Passive tree", guidance.Passive.Checklist, Nodes(guidance.Passive.TreeId));
        foreach (var skill in planned)
        {
            string name = SkillName(feature, skill);
            AddSection(rows, "skill." + skill.AbilityId, name, skill.Tree.Checklist, Nodes(skill.Tree.TreeId, name));
        }
        AddSection(rows, "bar", "Skill bar", guidance.BarChecklist, slot =>
            (stage.Bar[slot] ?? have[slot]) is { } skill ? $"{AbilityName(feature, skill, skill)} (slot {slot + 1})" : $"slot {slot + 1}");
        if (run.Gear is { } gear) AddGear(rows, run, gear);

        rows.Add(new HeadingRow("Next steps"));
        string site = run.Build.Source == LetFormat.Source ? "Last Epoch Tools" : "Maxroll";
        AddSteps(rows, "passive", "Passive tree", guidance.Passive, site, Nodes(guidance.Passive.TreeId));
        foreach (var skill in planned)
        {
            string name = SkillName(feature, skill);
            AddSteps(rows, "skill." + skill.AbilityId, name, skill.Tree, site, Nodes(skill.Tree.TreeId, name));
        }
    }

    // Done out of total in its state's colour, then the open items by name under it.
    private static void AddSection(List<MenuRow> rows, string key, string label, ChecklistSection list, Func<int, string> name)
    {
        string count = $"{list.Done} / {list.Total}";
        var state = list.Done == list.Total && list.OffPlan.Count == 0 ? StatusState.Active : StatusState.Waiting;
        rows.Add(new LiveText(label, () => (count, state)) { Key = "check." + key });
        var parts = new List<string>();
        Items(parts, S.ToPlaceColour.Label, list.Missing, name);
        Items(parts, S.PartialColour.Label, list.Partial, name);
        Items(parts, S.OffPlanColour.Label, list.OffPlan, name);
        if (list.Unknown > 0) parts.Add($"Not in this game version: {list.Unknown}.");
        if (parts.Count == 0) return;
        string text = string.Join(" ", parts);
        rows.Add(new HintRow(() => text) { Key = "check." + key + ".items" });
    }

    private static void Items(List<string> parts, string lead, List<int> ids, Func<int, string> name)
    {
        if (ids.Count > 0) parts.Add($"{lead}: {string.Join(", ", ids.Select(name))}.");
    }

    // The tree's next steps, numbered; the planner's own words when it gives no order.
    private static void AddSteps(List<MenuRow> rows, string key, string label, TreeGuide tree, string site, Func<int, string> name)
    {
        string text;
        if ((tree.Flags & TreeFlags.NoOrder) != 0) text = $"{label}: {Words.NoOrder(site)}";
        else if (tree.Next.Count > 0) text = $"{label}: " + string.Join(", ", tree.Next.Select(step => $"{step.Number}. {name(step.Node)} +{step.Add}")) + ".";
        else return;
        rows.Add(new HintRow(() => text) { Key = "steps." + key });
    }

    // Node names only change with a game update.
    private static readonly Dictionary<(string, byte), string> s_nodeNames = new();

    private static string NodeName(Core.Feature feature, string treeId, byte node)
    {
        if (s_nodeNames.TryGetValue((treeId, node), out var cached)) return cached;
        string fallback = $"node {node}";
        if (treeId.Length == 0 || !feature.Has(GameDataGroup)) return fallback;
        string name = fallback;
        try
        {
            var trees = GlobalTreeData.get();
            var data = Game.Alive(trees) ? trees!.getTreeData(treeId)?.getNode(node) : null;
            if (data == null) return fallback;
            if (!string.IsNullOrWhiteSpace(data.name)) name = data.name;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:node name", $"Planner: the node's name couldn't be read: {e.Message}", 30);
            return fallback;
        }
        s_nodeNames[(treeId, node)] = name;
        return name;
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

    // The planned bar beside the current one, slot by slot in its state's colour, for the character's build; made again
    // only when the guidance or a colour changes.
    private static Guidance? s_barFor;
    private static (Color, Color, Color, Color) s_barColours;
    private static readonly string[] s_barText = new string[Stage.BarSlots];

    private static bool BarShown(string id)
    {
        if (s_run is not { Guidance: { } guidance, Build: { } build } run || build.Id != id) return false;
        var colours = (S.Colour(S.MatchColour), S.Colour(S.PartialColour), S.Colour(S.ToPlaceColour), S.Colour(S.OffPlanColour));
        if (s_barFor == guidance && s_barColours == colours) return true;
        s_barFor = guidance;
        s_barColours = colours;
        var stage = build.Stages[run.Stage];
        var have = run.Allocation.Bar;
        for (int i = 0; i < Stage.BarSlots; i++)
        {
            string planned = stage.Bar[i] is { } p ? AbilityName(run.Feature, p, p) : "nothing planned";
            string now = have[i] is { } h ? AbilityName(run.Feature, h, h) : "empty";
            string text = $"{planned} · now {now}";
            s_barText[i] = guidance.Bar[i] is { } state ? $"<color=#{ColorUtility.ToHtmlStringRGB(StateColour(state))}>{text}</color>" : text;
        }
        return true;
    }

    private static string BarText(int slot) => s_barText[slot] ?? string.Empty;

    private static Color StateColour(GuidanceState state) => state switch
    {
        GuidanceState.Matches => S.Colour(S.MatchColour),
        GuidanceState.Partial => S.Colour(S.PartialColour),
        GuidanceState.ToPlace => S.Colour(S.ToPlaceColour),
        _ => S.Colour(S.OffPlanColour),
    };

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
        // The master switch and the outline settings share the first sub-tab, Overlay, the colours closing it under their
        // heading; every other section has its own. Each interface's page sits under Interfaces in the tree, and the next
        // point's settings close Interfaces' page and each tree's under their heading.
        var overlay = new MenuPage(S.Overlay.Title);
        entry.Pages.Add(overlay);
        MenuPage? interfaces = null;
        foreach (var section in S.Group.Sections)
        {
            if (section == S.Colours) continue;
            bool first = section == S.Main || section == S.Overlay;
            bool gearPage = Array.IndexOf(S.GearPages, section) >= 0;
            bool panel = gearPage || Array.Exists(S.Panels, p => p.Section == section);
            var page = first ? overlay : new MenuPage(section.Title) { InTree = !panel };
            var next = Array.Find(S.Panels.Append(S.Interfaces).ToArray(), p => p.Section == section)?.Next;
            foreach (var setting in section.Settings)
            {
                if (next != null && next.All.Contains(setting)) continue;
                if (S.InventoryHeadings.TryGetValue(setting, out string? heading)) page.Rows.Add(new HeadingRow(heading));
                page.Rows.Add(new SettingRow(setting) { EnabledWhen = EnabledWhen(setting) });
            }
            if (next != null)
            {
                page.Rows.Add(new HeadingRow("Next point"));
                foreach (var setting in next.All) page.Rows.Add(new SettingRow(setting) { EnabledWhen = EnabledWhen(setting) });
            }
            if (first) continue;
            entry.Pages.Add(page);
            if (section == S.Interfaces.Section) interfaces = page;
            else if (panel)
            {
                interfaces?.Children.Add(page);
                if (gearPage)
                {
                    // Neither page's panel has a game call of its own: both open the inventory, the Blessings page then
                    // its Blessings tab.
                    bool blessings = section == S.Blessings;
                    page.Actions.Add(new ActionButton("Open", $"planner.open.{section.Id}")
                    {
                        OnClick = () => OpenInventory(blessings),
                        EnabledWhen = CanOpenInGame,
                        Tip = section == S.Blessings
                            ? "Opens the inventory's Blessings tab in the game, behind this menu. Nothing is moved or chosen."
                            : "Opens the inventory in the game, behind this menu. Nothing is moved or equipped.",
                    });
                    continue;
                }
                var look = Array.Find(S.Panels, p => p.Section == section)!;
                page.Actions.Add(new ActionButton("Open", $"planner.open.{section.Id}")
                {
                    OnClick = () => OpenInGame(look),
                    EnabledWhen = CanOpenInGame,
                    Tip = OpenTip(look),
                });
            }
        }
        overlay.Rows.Add(new HeadingRow(S.Colours.Title));
        foreach (var setting in S.Colours.Settings) overlay.Rows.Add(new SettingRow(setting));
        return entry;
    }

    private static GUIStyle? s_sampleText;
    private static readonly (float X, float Y)[] s_outline = { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) };

    /// <summary>The label background choice's example: a next point label on that background, as a tree draws it.</summary>
    internal static void LabelSample(string back, Rect r)
    {
        if (back == "game" && Marks.CounterSprite() is { } sprite && sprite.texture != null)
        {
            var area = sprite.textureRect;
            var texture = sprite.texture;
            GUI.color = Marks.CounterTint;
            GUI.DrawTextureWithTexCoords(r, texture, new Rect(area.x / texture.width, area.y / texture.height, area.width / texture.width, area.height / texture.height));
        }
        else if (back != "none" && Marks.PlateTexture is { } plate)
        {
            GUI.color = Marks.Plate;
            Sliced(r, plate, Mathf.Min(r.height / 2f, 6f));
        }
        GUI.color = Color.white;
        s_sampleText ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 12, padding = new RectOffset() };
        string text = S.Interfaces.Next!.Slash ? "1/5" : "1 < 5";
        if (back == "none")
        {
            s_sampleText.normal.textColor = Color.black;
            foreach (var (x, y) in s_outline) GUI.Label(new Rect(r.x + x, r.y + y, r.width, r.height), text, s_sampleText);
        }
        s_sampleText.normal.textColor = S.Colour(S.PartialColour);
        GUI.Label(r, text, s_sampleText);
    }

    // A rounded square texture stretched with its corners kept round: nine parts, the corners at `corner` pixels and the
    // texture's outer third as their source.
    private static void Sliced(Rect r, Texture texture, float corner)
    {
        const float edge = 1f / 3f;
        float[] xs = { r.x, r.x + corner, r.xMax - corner, r.xMax }, ys = { r.y, r.y + corner, r.yMax - corner, r.yMax };
        float[] us = { 0f, edge, 1f - edge, 1f };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                GUI.DrawTextureWithTexCoords(new Rect(xs[i], ys[j], xs[i + 1] - xs[i], ys[j + 1] - ys[j]), texture,
                    new Rect(us[i], 1f - us[j + 1], us[i + 1] - us[i], us[j + 1] - us[j]));
    }

    // The game's own way into each interface, as its keys and buttons open them; the menu stays open over it. Opening a
    // panel only shows it: nothing is spent or chosen.
    private static string OpenTip(S.PanelLook look) => (look == S.PassiveLook ? "Opens the passive tree"
        : look == S.SkillTreeLook ? "Opens the tree of your first specialised skill"
        : look == S.SkillsPageLook ? "Opens the Skills & Specializations page"
        : look == S.WeaverLook ? "Opens the Woven faction page, whose Weaver Tree button shows the tree,"
        : "Opens the skill selection for bar slot 1") + " in the game, behind this menu. Nothing is spent or chosen.";

    private static bool CanOpenInGame() => s_run is { Character: not null } run && run.Feature.Has(OpenUiGroup) && Game.Alive(UIBase.instance);

    private static void OpenInGame(S.PanelLook look)
    {
        var ui = UIBase.instance;
        if (!CanOpenInGame() || !Game.Alive(ui)) return;
        try
        {
            // Whatever else is open would sit over the interface, or it over them.
            ui!.CloseAllPanels();
            if (look != S.SkillChoiceLook && AbilityBarSelectorManager.instance is { IsMenuOpened: true } open && Game.Alive(open)) open.CloseMenu(false);
            if (look == S.PassiveLook) ui!.OpenPassivePanel(false);
            else if (look == S.SkillTreeLook) ui!.openSkills(false, FirstSpecialised());
            else if (look == S.SkillsPageLook) ui!.openSkills(false, null);
            else if (look == S.WeaverLook) ui!.OpenWeaverRankPanel(false, false);
            else if (AbilityBarSelectorManager.instance is { } selector && Game.Alive(selector))
            {
                // The selection belongs to a bar slot; the first one.
                selector.abilityClicked = 1;
                selector.OpenMenu();
            }
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:open", $"Planner: the game's interface couldn't be opened: {e.Message}", 30);
        }
    }

    private static void OpenInventory(bool blessings)
    {
        var ui = UIBase.instance;
        if (!CanOpenInGame() || !Game.Alive(ui)) return;
        try
        {
            ui!.CloseAllPanels();
            ui.openInventory(false, false, false, 0, false);
            // The tab is the inventory's own; it's chosen once the inventory has drawn it.
            if (blessings) s_blessingsTabUntil = Clock.Now + BlessingsTabSeconds;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:open", $"Planner: the game's interface couldn't be opened: {e.Message}", 30);
        }
    }

    // While set, the Blessings tab is chosen as soon as the inventory shows it.
    private static float s_blessingsTabUntil;
    private const float BlessingsTabSeconds = 2f;
    private const string BlessingsTabName = "BlessingTab";

    /// <summary>Chooses the inventory's Blessings tab after the Blessings page's Open, as its own tab would.</summary>
    private static void ChooseBlessingsTab()
    {
        if (s_blessingsTabUntil <= 0f) return;
        if (Clock.Now > s_blessingsTabUntil)
        {
            s_blessingsTabUntil = 0f;
            return;
        }
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<TabUIElement>(), false))
        {
            var tab = obj.TryCast<TabUIElement>();
            if (!Game.Alive(tab) || tab!.name != BlessingsTabName || !tab.gameObject.activeInHierarchy || !Game.Alive(tab.controller)) continue;
            s_blessingsTabUntil = 0f;
            try
            {
                tab.controller.Select(tab, true);
            }
            catch (Exception e)
            {
                Log.ErrorThrottled("planner:open", $"Planner: the Blessings tab couldn't be chosen: {e.Message}", 30);
            }
            return;
        }
    }

    // The specialised skill in the lowest slot, for its tree; null opens the Skills page without one.
    private static Ability? FirstSpecialised()
    {
        var skills = Game.LocalTree?.specialisedSkillTrees;
        Ability? first = null;
        int lowest = int.MaxValue;
        for (int i = 0; skills != null && i < skills.Count; i++)
        {
            var skill = skills[i];
            if (skill == null || !Game.Alive(skill.ability) || skill.slot >= lowest) continue;
            lowest = skill.slot;
            first = skill.ability;
        }
        return first;
    }

    // A row that only shapes another setting's output is greyed while that setting is off; an interface's look and next
    // point while it uses Interfaces'.
    private static Func<bool>? EnabledWhen(Setting setting)
    {
        if (setting == S.HoverDelay || setting == S.HintPosition) return () => S.HoverHints.Value;
        if (setting == S.InventoryMatches || setting == S.InventoryIdols || setting == S.InventoryIconSize || setting == S.InventoryIconCorner
            || setting == S.InventoryIconGlyph || setting == S.InventoryIconBackground || setting == S.InventoryIconOpacity
            || setting == S.InventoryIconOffsetX || setting == S.InventoryIconOffsetY || setting == S.InventoryTooltipDelay
            || setting == S.InventoryHidePrompts || setting == S.InventoryHints) return () => S.InventoryMarks.Value;
        if (setting == S.InventoryHintPosition || setting == S.InventoryHintMissing || setting == S.InventoryHintWorn)
            return () => S.InventoryMarks.Value && S.InventoryHints.Value;
        if (setting == S.InventoryAltar) return () => S.InventoryMarks.Value;
        if (setting == S.InventoryIdolIconSize || setting == S.InventoryIdolOffsetX || setting == S.InventoryIdolOffsetY)
            return () => S.InventoryMarks.Value && S.InventoryIdols.Value;
        foreach (var look in S.Panels.Append(S.Interfaces))
        {
            Func<bool> own = look.Own is { } switchOn ? () => switchOn.Value : () => true;
            if (setting == look.FillOpacity) return () => own() && look.Fill.Value;
            if (setting == look.Opacity || setting == look.Size || setting == look.Fill) return look.Own != null ? own : null;
            if (look.Next is not { } next) continue;
            if (setting == next.LabelSize || setting == next.LabelFormat || setting == next.LabelSide || setting == next.LabelBackground || setting == next.LabelX || setting == next.LabelY) return () => own() && next.Label.Value;
            if (setting == next.Colour) return () => own() && next.OwnColour.Value;
            if (setting == next.Steps || setting == next.Label || setting == next.OwnColour) return look.Own != null ? own : null;
        }
        return null;
    }
}
