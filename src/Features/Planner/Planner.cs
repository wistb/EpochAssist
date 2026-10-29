using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;
using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;
using S = EpochAssist.Features.PlannerSettings;

namespace EpochAssist.Features;

/// <summary>The game panel a planner guides, when one is open. Runtime only, never stored.</summary>
internal enum GuidedPanel { None, Passive, SkillTree, SkillChoice, SkillsPage, Weaver }

/// <summary>
/// Guides the character towards a planner build: the active build and stage for the character in play, the character's
/// trees and bar as the game holds them, and the comparison of the two. It only reads the game: it never spends,
/// removes or chooses anything, and writes nothing to the character or its save.
/// </summary>
internal static partial class Planner
{
    // Each mark kind's way into the game in its own group, so a game change makes only that kind unavailable. The
    // character's trees have no group: without them nothing can be guided.
    public const string PassiveUiGroup = "passive ui";
    public const string SkillUiGroup = "skill ui";
    public const string SkillChoiceGroup = "skill choice";
    public const string SkillsPageGroup = "skills page";
    public const string GameDataGroup = "game data";
    public const string OpenUiGroup = "open ui";
    public const string ItemDataGroup = "item data";
    public const string ItemTooltipGroup = "item tooltip";
    public const string InventoryUiGroup = "inventory ui";
    public const string BlessingUiGroup = "blessing ui";
    public const string WeaverUiGroup = "weaver ui";
    public const string WeaverItemsGroup = "weaver items";

    private const float FetchPollSeconds = 0.1f;
    private const float PanelSeconds = 0.1f;
    // With no panel open the allocation only feeds the tab's checklist; with one open it's read every frame.
    private const float AllocationSeconds = 0.5f;

    // Everything one run keeps. Made in Start, dropped in Shutdown.
    private sealed class Run
    {
        public Feature Feature = null!;
        public CharacterKey? Character;
        public Build? Build;
        public int Stage;
        public int LibraryGeneration = -1;
        public readonly Allocation Allocation = new();
        public bool HasAllocation;
        public GuidedPanel Open;
        public Guidance? Guidance;
        // What the guidance was computed from; any change computes it again.
        public ulong GuidedSignature;
        public Build? GuidedBuild;
        public int GuidedStage = -1;
        // The passive tree's marks and what they were made from; any change makes them again.
        public readonly List<PlannerMark> PassiveMarks = new();
        public IntPtr MarkedTree;
        public int MarkedPage = -1;
        public int MarkedNodes = -1;
        public Guidance? MarkedGuidance;
        public MarkLook MarkedLook;
        // The open skill tree's marks and what they were made from, as for the passive tree.
        public readonly List<PlannerMark> SkillMarks = new();
        public IntPtr SkillMarkedTree;
        public string? SkillTreeId;
        public int SkillMarkedNodes = -1;
        public Guidance? SkillMarkedGuidance;
        public MarkLook SkillMarkedLook;
        public string? SkillWords;
        // The skill choice marks (selection panel or Skills page) and what they were made from.
        public readonly List<PlannerMark> ChoiceMarks = new();
        public GuidedPanel ChoicePanel;
        public int ChoiceKey = int.MinValue;
        public Guidance? ChoiceMarkedGuidance;
        public MarkLook ChoiceMarkedLook;
        public (int Class, int Mastery) MasteryWordsFor = (-1, -1);
        public string? MasteryWords;
        // The character's gear and its comparison with the stage's planned gear, made again when either changes.
        public readonly EquippedSide Equipped = new();
        public GearGuidance? Gear;
        public ulong GearSignature;
        public Build? GearBuild;
        public int GearStage = -1;
        public bool InventoryOpen;
        // The weaver tree's marks and what they were made from.
        public readonly List<PlannerMark> WeaverMarks = new();
        public IntPtr WeaverMarkedTree;
        public int WeaverMarkedNodes = -1;
        public TreeGuide? WeaverMarkedGuide;
        public SortedDictionary<int, ItemGuide>? WeaverMarkedItems;
        // The open weaver panel's own counts compared with the plan, and the nodes whose points aren't confirmed yet.
        public TreeGuide? WeaverPanelGuide, WeaverPanelBase;
        public ulong WeaverPanelSignature;
        public readonly HashSet<byte> WeaverUnconfirmed = new();
        public MarkLook WeaverMarkedLook;
        public string StatusDetail = "Off";
        public StatusState StatusState = StatusState.Off;
    }

    private static Run? s_run;
    // Each passive tree's nodes and the mastery they need; tree data only changes with a game update.
    private static readonly Dictionary<string, Dictionary<byte, byte>?> s_requiredMastery = new();

    public static void Start(Feature feature)
    {
        var run = new Run { Feature = feature };
        s_run = run;
        Library.Load();
        Marks.Sweep();
        SweepIconHolder();
        AddGuideSearch();
        SweepTooltip();
        Scheduler.Every(feature, "planner fetch", FetchPollSeconds, () => PollFetch());
        Scheduler.Every(feature, "planner panels", PanelSeconds, () => { if (s_run == run) Tick(run); }, startNow: true);
        Scheduler.Every(feature, "planner allocation", AllocationSeconds, () =>
        {
            if (s_run == run && run.Open == GuidedPanel.None) ReadAndCompare(run);
            if (s_run == run && !run.InventoryOpen) ReadGear(run);
        });
        Actions.Register(feature.Id, "planner.compare", "Compare with build", CompareHovered);
    }

    /// <summary>While a guided panel is open the allocation is checked every frame, so a spent point shows at once.</summary>
    public static void Frame()
    {
        if (s_run is { Open: not GuidedPanel.None } run)
        {
            ReadAndCompare(run);
            Marks.Pulse();
        }
        // A swapped item recolours its icon in the same frame.
        if (s_run is { } menuRun) MenuHover(menuRun);
        long guideStart = s_costOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        PollIcons();
        GuideFrame();
        FilterFrame();
        if (s_costOn) s_costTicks += System.Diagnostics.Stopwatch.GetTimestamp() - guideStart;
        // The icons follow at the tick (0.1 s): made again every frame they'd cost more than the rest together.
        if (s_run is { InventoryOpen: true } open)
        {
            ReadGear(open);
            GearHover(open);
        }
    }

    public static void Shutdown()
    {
        // A running request's result is dropped, so its "Fetching" line would never end.
        if (Fetch.Busy)
        {
            s_outcome = null;
            s_fetching = null;
            ImportChanged();
        }
        Fetch.Drop();
        if (s_run is { } stopping) ClearHint(stopping);
        s_hovered = null;
        ClearGearMarks();
        s_gearHover = null;
        ForgetMenuAnchor();
        Marks.Release();
        Panels.Forget();
        ForgetGear();
        ForgetGuidePage();
        // Made filters stay in the game's folder; only what the page holds is dropped.
        ForgetFilterPage();
        LootFilterStore.Forget();
        LootFilterGame.Forget();
        LootFilterBase.Forget();
        RemoveGuideSearch();
        s_run = null;
    }

    /// <summary>A new scene destroys the game's panels with the marks under them.</summary>
    public static void OnSceneChanged()
    {
        if (s_run is { } run0) ClearHint(run0);
        s_hovered = null;
        // The tooltip manager goes with the scene: nothing to close.
        s_tipItem = null;
        ClearGearMarks();
        s_gearHover = null;
        Marks.ClearAll();
        Panels.Forget();
        ForgetMade();
        if (s_run is { } run)
        {
            run.Open = GuidedPanel.None;
            run.InventoryOpen = false;
            ClearPassiveMarks(run);
            ClearSkillMarks(run);
            ClearChoiceMarks(run);
            ClearWeaverMarks(run);
        }
    }

    // ---- read-outs for the tab and the dev bridge ----

    /// <summary>The character in play, for the log: mode and slot, never its name.</summary>
    public static string? CharacterLabel => s_run?.Character?.Label;
    public static Build? ActiveBuild => s_run?.Build;
    public static int ActiveStage => s_run?.Stage ?? 0;
    /// <summary>The character's trees and bar as last read, or null before the first read.</summary>
    public static Allocation? CurrentAllocation => s_run is { HasAllocation: true } run ? run.Allocation : null;
    public static Guidance? CurrentGuidance => s_run?.Guidance;
    public static GuidedPanel OpenPanel => s_run?.Open ?? GuidedPanel.None;
    /// <summary>The stage's gear against the character's, or null before the first read or with no gear planned.</summary>
    public static GearGuidance? CurrentGear => s_run?.Gear;

    // ---- import ----

    // Pasted text is parsed on the main thread, so only up to this size.
    private const int MainThreadParseBytes = 1024 * 1024;

    // Kept outside the run, so the tab imports while the feature waits for a character.
    private static string? s_outcome;
    // An import whose id the library already has, until the player picks Replace or Keep.
    private static Build? s_pending;
    // A Last Epoch Tools link whose request failed: its build JSON pasted by hand keeps the link.
    private static Paste? s_failedLetLink;
    // The link whose request is running, for an attempt that ended in an exception and carries no paste.
    private static Paste? s_fetching;

    /// <summary>The outcome line of the last import attempt, or null before the first.</summary>
    public static string? ImportOutcome => s_outcome;

    /// <summary>The build data address to open by hand after a failed Last Epoch Tools link, or null.</summary>
    public static string? BuildDataLink => s_failedLetLink?.Id is { } code ? PasteKind.LetDataUrl(code) : null;

    /// <summary>
    /// The one paste handler, for the tab's button and the dev paste verb. A link of a shown, switched-on site starts its
    /// one request; a Maxroll export or Last Epoch Tools build JSON is parsed here with none, checked against the game
    /// data and added to the library. A build the library already has waits for Replace or Keep.
    /// </summary>
    public static PasteResult Paste(string? text)
    {
        Library.EnsureLoaded();
        s_pending = null;
        var result = Handle(PasteKind.Recognise(text));
        s_outcome = result.Message;
        ImportChanged();
        return result;
    }

    private static PasteResult Handle(Paste paste)
    {
        if (SiteOf(paste) is { } site)
        {
            // A site whose link import isn't offered treats its links as any other text.
            if (!PlannerSites.Shown(site)) return new PasteResult(Words.NotABuild);
            if (!Fetch.Start(site, paste)) return new PasteResult(Words.Busy);
            s_failedLetLink = null;
            s_fetching = paste;
            return new PasteResult(Words.Fetching(site)) { Started = true };
        }
        if (paste.Kind is not (PasteSource.MaxrollExport or PasteSource.LetJson) || paste.Text.Length > MainThreadParseBytes)
            return new PasteResult(Words.NotABuild);
        var link = paste.Kind == PasteSource.LetJson ? s_failedLetLink : null;
        var parsed = paste.Kind == PasteSource.MaxrollExport
            ? MaxrollFormat.ParseExport(paste.Text, DateTime.Now)
            : LetFormat.Parse(paste.Text, link?.PageUrl, link?.Id, DateTime.Now);
        if (parsed.Build is not { } build) return new PasteResult(Words.NotABuild);
        if (link != null) s_failedLetLink = null;
        return new PasteResult(Take(build)) { Build = build };
    }

    private static PlannerSite? SiteOf(Paste paste) => paste.Kind switch
    {
        PasteSource.MaxrollLink => PlannerSite.Maxroll,
        PasteSource.LetLink => PlannerSite.LastEpochTools,
        _ => null,
    };

    /// <summary>
    /// Picks up a finished fetch on the main thread and hands its build to the same add path as a pasted export. Run by
    /// the feature's poll, the tab while it shows and the dev paste verb; whichever comes first takes it.
    /// </summary>
    public static FetchAttempt? PollFetch()
    {
        if (Fetch.Poll() is not { } attempt) return null;
        if (attempt.Paste == null && s_fetching is { } paste && SiteOf(paste) is { } site)
        {
            attempt.Paste = paste;
            attempt.Site = site;
        }
        s_fetching = null;
        if (attempt.Build is { } build)
        {
            s_outcome = Take(build);
        }
        else
        {
            s_outcome = Words.Outcome(attempt);
            // Only the outcomes whose words point at the build data link offer it.
            bool fallback = attempt.Site == PlannerSite.LastEpochTools && attempt.State is FetchState.TimedOut or FetchState.Refused or FetchState.Failed;
            s_failedLetLink = fallback ? attempt.Paste : null;
        }
        ImportChanged();
        return attempt;
    }

    /// <summary>Adds an already parsed build as a paste would, with no request; the outcome shows on the tab.</summary>
    public static string Add(Build build)
    {
        s_outcome = Take(build);
        ImportChanged();
        return s_outcome;
    }

    // A build with a new id is added; one the library has waits for the player's choice.
    private static string Take(Build build)
    {
        Library.EnsureLoaded();
        if (Library.Find(build.Id) is { } old)
        {
            s_pending = build;
            return Words.AlreadyInLibrary(old);
        }
        return Store(build);
    }

    // Checked against the game data before it's written, so the file keeps the ids the check filled in.
    private static string Store(Build build)
    {
        s_pending = null;
        ForgetResolution(build.Id);
        var resolution = Resolve(build);
        if (build.Source == LetFormat.Source && build.Name == LetFormat.DefaultName && LetSkillsName(build) is { } named)
        {
            build.Name = named;
            if (build.Stages.Count == 1 && build.Stages[0].Name == LetFormat.DefaultName) build.Stages[0].Name = named;
        }
        if (!Library.Add(build))
        {
            ForgetResolution(build.Id);
            return Words.NotSaved(build);
        }
        View.Selected = build.Id;
        int unknown = resolution.UnknownNodes.Count + resolution.UnknownSkills.Count;
        return !resolution.Unread && unknown > 0 ? Words.Imported(build) + " " + Words.UnknownIds(resolution) : Words.Imported(build);
    }

    /// <summary>A Last Epoch Tools build's name from its mastery and first three specialised skills, by the game's own
    /// names ("Lich · Marrow Shards, Flay, Reaper Form"); null until the game's data can be read.</summary>
    private static string? LetSkillsName(Build build)
    {
        if (s_run?.Feature is not { } feature || !feature.Has(GameDataGroup) || build.Stages.Count == 0) return null;
        var stage = build.Stages[0];
        string mastery = MasteryName(feature, stage.ClassId, stage.Mastery);
        if (mastery.StartsWith("mastery ", StringComparison.Ordinal)) return null;
        var skills = stage.Skills.Where(s => s.AbilityId.Length > 0).Take(3).Select(s => AbilityName(feature, s.AbilityId, string.Empty)).Where(n => n.Length > 0).ToList();
        return skills.Count > 0 ? $"{mastery} · {string.Join(", ", skills)}" : mastery;
    }

    // Builds imported before they were named keep the default; each gets its guide's name or its skills once the game's
    // data can be read, and the library saves it.
    private static void NameUntitledLetBuilds()
    {
        foreach (var build in Library.Builds.ToList())
        {
            if (build.Source != LetFormat.Source || build.Name != LetFormat.DefaultName) continue;
            string? slug = MaxrollFormat.Parse(build.Raw) is System.Text.Json.Nodes.JsonObject raw ? Build.Str(raw["guide"]) : null;
            if ((LetFormat.GuideTitle(slug) ?? LetSkillsName(build)) is { } name) Library.Rename(build.Id, name);
        }
    }

    /// <summary>Replaces the library's build with the waiting import.</summary>
    public static void ReplacePending()
    {
        if (s_pending is not { } build) return;
        s_outcome = Store(build);
        ImportChanged();
    }

    /// <summary>Keeps the library's build and drops the waiting import.</summary>
    public static void KeepPending()
    {
        if (s_pending is not { } build) return;
        s_pending = null;
        s_outcome = Words.Kept(Library.Find(build.Id)?.Name ?? build.Name);
        ImportChanged();
    }

    // ---- the run ----

    // Every 0.1 s: the character, its build, the open panel and the status words.
    private static void Tick(Run run)
    {
        try
        {
            var key = Profiles.Key;
            if (key != run.Character)
            {
                run.Character = key;
                run.HasAllocation = false;
                run.Guidance = null;
                run.Gear = null;
                run.Equipped.Signature = 0;
                run.LibraryGeneration = -1;
                ClearGearMarks();
                Marks.ClearAll();
                ClearPassiveMarks(run);
                ClearSkillMarks(run);
                ClearChoiceMarks(run);
            }
            if (run.LibraryGeneration != Library.Generation)
            {
                run.LibraryGeneration = Library.Generation;
                var binding = Library.Binding(key);
                run.Build = Library.Find(binding?.Build);
                run.Stage = run.Build != null ? Math.Clamp(binding!.Value.Stage, 0, run.Build.Stages.Count - 1) : 0;
            }
            bool inventory = S.Enabled.Value && key != null && run.Build != null && run.Feature.Has(InventoryUiGroup) && UIBase.instance is { } ui && Game.Alive(ui) && ui.IsInventoryOpen();
            if (inventory != run.InventoryOpen)
            {
                run.InventoryOpen = inventory;
                if (inventory) ReadGear(run);
            }
            var open = S.Enabled.Value && key != null && run.Build != null ? Panels.Detect(run.Feature) : GuidedPanel.None;
            if (open != run.Open)
            {
                run.Open = open;
                if (open != GuidedPanel.None) ReadAndCompare(run);
            }
            // Here too, for a page or tree change, a switch turned off or the panel closing.
            MarkPassive(run);
            MarkSkillTree(run);
            MarkSkillChoice(run);
            // Here too, for the inventory closing or a switch turned off.
            MarkGear(run);
            MarkWeaver(run);
            ChooseBlessingsTab();
            if (!run.InventoryOpen) GearHover(run);
            Hover(run);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:tick", $"Planner: reading the character and its panels failed: {e.Message}", 30);
        }
        UpdateStatus(run);
    }

    private static void ReadAndCompare(Run run)
    {
        if (!S.Enabled.Value || run.Character == null || run.Build == null) return;
        try
        {
            ReadAllocation(run);
            if (run.HasAllocation) CompareIfChanged(run);
            // A spent point recolours the marks in the same frame.
            if (run.Open == GuidedPanel.Passive && run.Guidance != run.MarkedGuidance) MarkPassive(run);
            else if (run.Open == GuidedPanel.SkillTree && run.Guidance != run.SkillMarkedGuidance) MarkSkillTree(run);
            else if (run.Open is GuidedPanel.SkillChoice or GuidedPanel.SkillsPage && run.Guidance != run.ChoiceMarkedGuidance) MarkSkillChoice(run);
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:allocation", $"Planner: reading the character's trees failed: {e.Message}", 30);
        }
    }

    // The tree data is absent during a zone load (seconds online): the last allocation is kept, marked stale, and never
    // replaced by an empty one.
    private static void ReadAllocation(Run run)
    {
        var tree = Game.LocalTree;
        var allocation = run.Allocation;
        if (tree == null)
        {
            allocation.Stale = run.HasAllocation;
            return;
        }
        allocation.Stale = false;
        ulong signature = Signature(tree);
        if (run.HasAllocation && signature == allocation.Signature) return;
        Fill(allocation, tree);
        allocation.Signature = signature;
        run.HasAllocation = true;
    }

    // The character's gear, and the stage's planned gear compared with it when either changed.
    private static void ReadGear(Run run)
    {
        if (!S.Enabled.Value || run.Character == null || run.Build is not { } build || !run.Feature.Has(InventoryUiGroup)) return;
        try
        {
            if (!ReadEquipped(run.Equipped, run.Feature.Has(WeaverItemsGroup))) return;
            if (run.Gear != null && run.GearSignature == run.Equipped.Signature && run.GearBuild == build && run.GearStage == run.Stage) return;
            // Items made for another stage or build won't be hovered again.
            if (run.GearBuild != build || run.GearStage != run.Stage) ForgetMade();
            var gear = GearOf(build, run.Stage);
            run.Gear = gear.Empty ? null : CompareGear(gear, run.Equipped);
            run.GearSignature = run.Equipped.Signature;
            run.GearBuild = build;
            run.GearStage = run.Stage;
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("planner:gear", $"Planner: reading the character's gear failed: {e.Message}", 30);
        }
    }

    // FNV-1a over everything the guidance reads: class, mastery, level, every node with points, each specialised
    // skill and its nodes, and the bar.
    private static ulong Signature(LocalTreeData tree)
    {
        ulong h = 14695981039346656037UL;
        void Mix(int value)
        {
            h ^= (uint)value;
            h *= 1099511628211UL;
        }
        var cls = tree.characterClass;
        Mix(Game.Alive(cls) ? (int)cls!.classID : -1);
        Mix(tree.chosenMastery);
        Mix(tree.characterLevel);
        MixNodes(tree.passiveTree?.nodes, ref h);
        var skills = tree.specialisedSkillTrees;
        for (int i = 0; skills != null && i < skills.Count; i++)
        {
            var skill = skills[i];
            if (skill == null) continue;
            Mix(skill.slot);
            Mix(skill.treeID?.GetHashCode() ?? 0);
            MixNodes(skill.nodes, ref h);
        }
        var bar = PlayerModel.Slots;
        for (int i = 0; bar != null && i < bar.Count; i++) Mix(bar[i]?.AbilityId?.GetHashCode() ?? 0);
        return h;
    }

    private static void MixNodes(Il2CppSystem.Collections.Generic.List<LocalTreeData.NodeData>? nodes, ref ulong h)
    {
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null || node.pointsAllocated == 0) continue;
            h ^= (uint)(node.id << 8 | node.pointsAllocated);
            h *= 1099511628211UL;
        }
    }

    private static void Fill(Allocation allocation, LocalTreeData tree)
    {
        var cls = tree.characterClass;
        allocation.ClassId = Game.Alive(cls) ? (int)cls!.classID : -1;
        allocation.Mastery = tree.chosenMastery;
        allocation.Level = tree.characterLevel;
        FillNodes(allocation.Passive, tree.passiveTree?.nodes);
        allocation.Skills.Clear();
        var skills = tree.specialisedSkillTrees;
        for (int i = 0; skills != null && i < skills.Count; i++)
        {
            var skill = skills[i];
            if (skill == null || string.IsNullOrEmpty(skill.treeID)) continue;
            var have = new AllocatedSkill { Slot = skill.slot };
            FillNodes(have.Points, skill.nodes);
            allocation.Skills[skill.treeID] = have;
        }
        var bar = PlayerModel.Slots;
        for (int i = 0; i < allocation.Bar.Length; i++)
        {
            string? id = bar != null && i < bar.Count ? bar[i]?.AbilityId : null;
            allocation.Bar[i] = id == EmptySlotId ? null : id;
        }
    }

    // The ability the game puts in a slot the player emptied.
    private const string EmptySlotId = "na28";

    // Entries at 0 points (left by a respec or a refused click) aren't allocated.
    private static void FillNodes(Dictionary<byte, byte> into, Il2CppSystem.Collections.Generic.List<LocalTreeData.NodeData>? nodes)
    {
        into.Clear();
        for (int i = 0; nodes != null && i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node == null || node.pointsAllocated == 0) continue;
            into[node.id] = node.pointsAllocated;
        }
    }

    // How many of each tree's next steps are worked out, for the tab's list; the marks label and pulse only the first.
    private const int NextStepCount = 5;

    private static void CompareIfChanged(Run run)
    {
        if (run.Guidance != null && run.GuidedSignature == run.Allocation.Signature && run.GuidedBuild == run.Build && run.GuidedStage == run.Stage) return;
        var build = run.Build!;
        var stage = build.Stages[run.Stage];
        var resolution = Resolve(build);
        run.Guidance = Compare(stage, resolution, run.Allocation, RequiredMastery(run.Feature, stage.Passive.TreeId), NextStepCount);
        run.GuidedSignature = run.Allocation.Signature;
        run.GuidedBuild = build;
        run.GuidedStage = run.Stage;
    }

    // Null when the game data can't be read: then no node is left out for a different mastery.
    private static IReadOnlyDictionary<byte, byte>? RequiredMastery(Feature feature, string treeId)
    {
        if (treeId.Length == 0 || !feature.Has(GameDataGroup)) return null;
        if (s_requiredMastery.TryGetValue(treeId, out var cached)) return cached;
        Dictionary<byte, byte>? map = null;
        var trees = GlobalTreeData.get();
        var data = Game.Alive(trees) ? trees!.getTreeData(treeId) : null;
        if (data?.nodes is { } nodes)
        {
            map = new Dictionary<byte, byte>();
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] is { } node) map[node.id] = node.requiredMastery;
            s_requiredMastery[treeId] = map;
        }
        return map;
    }

    // ---- status ----

    // The words are made here, ten times a second, so the menu and the HUD only read them.
    private static void UpdateStatus(Run run)
    {
        string detail;
        StatusState state;
        if (!S.Enabled.Value)
        {
            (detail, state) = ("Off", StatusState.Off);
        }
        else if (run.Character == null)
        {
            (detail, state) = (Words.NoCharacter, StatusState.Waiting);
        }
        else if (run.Build == null)
        {
            (detail, state) = ("No build for this character", StatusState.Waiting);
        }
        else
        {
            detail = $"Guiding \"{run.Build.Name}\" · stage {run.Stage + 1} of {run.Build.Stages.Count}";
            if (run.Allocation.Stale) detail += " · waiting for the zone";
            if (run.Guidance is { DifferentMastery: true }) detail += " · " + MasteryWords(run);
            if (Unavailable(run.Feature) is { } kinds) detail += $" · Unavailable after a game update: {kinds}";
            state = StatusState.Active;
        }
        run.StatusDetail = detail;
        run.StatusState = state;
    }

    // The mark kinds a game change took out, in the tab's words.
    private static string? Unavailable(Feature feature)
    {
        if (feature.MissingGroups.Count == 0) return null;
        var kinds = new List<string>();
        if (!feature.Has(PassiveUiGroup)) kinds.Add("passive tree");
        if (!feature.Has(SkillUiGroup)) kinds.Add("skill trees");
        if (!feature.Has(SkillChoiceGroup)) kinds.Add("skill choice");
        if (!feature.Has(SkillsPageGroup)) kinds.Add("Skills & Specializations page");
        if (!feature.Has(GameDataGroup)) kinds.Add("game data");
        // Without the items or the game's tooltip the icons still show their state.
        if (!feature.Has(ItemDataGroup) || !feature.Has(ItemTooltipGroup)) kinds.Add("item tooltips");
        if (!feature.Has(InventoryUiGroup)) kinds.Add("inventory");
        if (!feature.Has(BlessingUiGroup)) kinds.Add("blessings");
        if (!feature.Has(WeaverUiGroup)) kinds.Add("weaver tree");
        if (!feature.Has(WeaverItemsGroup)) kinds.Add("weaver items");
        if (!feature.Has(GuideIconsGroup)) kinds.Add("guide icons");
        if (!feature.Has(GuideTipsGroup)) kinds.Add("guide tooltips");
        if (!feature.Has(LootFilterGroup)) kinds.Add("loot filter");
        else if (!feature.Has(LootFilterPanelGroup)) kinds.Add("loot filter window refresh");
        return kinds.Count > 0 ? string.Join(", ", kinds) : null;
    }

    /// <summary>The status for the HUD, the menu and the bridge.</summary>
    public static FeatureStatus StatusOf(Feature feature)
    {
        var run = s_run;
        if (run == null) return FeatureStatus.Off(string.Empty);
        return run.StatusState == StatusState.Off ? FeatureStatus.Off(run.StatusDetail) : new FeatureStatus(run.StatusState, run.StatusDetail, null);
    }

    /// <summary>The page's status line.</summary>
    public static (string, StatusState?) StatusText() => s_run is { } run ? (run.StatusDetail, run.StatusState) : ("Off", StatusState.Off);

    // ---- the game's panels ----

    /// <summary>
    /// Finds the panels a planner guides: the passive tree (its panel manager's instance, while active), a skill's tree
    /// (shown on the Skills page), the bar's skill selection, and the Skills &amp; Specializations page. The Skills page
    /// has no instance field, so it's found by an object search, kept while it lives and repeated at most once a second.
    /// </summary>
    internal static class Panels
    {
        private const float FindSeconds = 1f;

        private static readonly Dictionary<Type, (UnityEngine.Object? Found, float NextFind)> s_found = new();

        public static T? Find<T>() where T : Component
        {
            s_found.TryGetValue(typeof(T), out var entry);
            if (entry.Found is T found && Game.Alive(found)) return found;
            if (Clock.Now < entry.NextFind) return null;
            T? live = null;
            foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true))
            {
                var t = obj.TryCast<T>();
                if (!Game.Alive(t)) continue;
                live = t;
                if (t!.gameObject.activeInHierarchy) break;
            }
            s_found[typeof(T)] = (live, Clock.Now + FindSeconds);
            return live;
        }

        public static bool Open(Component? c) => Game.Alive(c) && c!.gameObject.activeInHierarchy;

        /// <summary>Drops what was found (a new scene, a stop).</summary>
        public static void Forget() => s_found.Clear();

        /// <summary>The passive panel while it's open.</summary>
        public static PassivePanelManager? Passive()
        {
            var manager = PassivePanelManager.instance;
            return Open(manager) ? manager : null;
        }

        /// <summary>The Skills &amp; Specializations page while it's open.</summary>
        public static SkillsPanelManager? SkillsPage()
        {
            var page = Find<SkillsPanelManager>();
            return Open(page) ? page : null;
        }

        /// <summary>The skill tree open on the Skills page, or null.</summary>
        public static Il2Cpp.Tree? SkillTree(SkillsPanelManager? page)
        {
            var tree = Game.Alive(page) ? page!.CurrentlyOpenTree : null;
            return Open(tree) ? tree : null;
        }

        public static bool SkillChoiceOpen()
        {
            var selector = AbilityBarSelectorManager.instance;
            return Game.Alive(selector) && selector!.IsMenuOpened;
        }

        /// <summary>The guided panel open now; a panel whose group the game lacks counts as closed.</summary>
        public static GuidedPanel Detect(Feature feature)
        {
            if (feature.Has(PassiveUiGroup) && Passive() != null) return GuidedPanel.Passive;
            if (feature.Has(SkillChoiceGroup) && SkillChoiceOpen()) return GuidedPanel.SkillChoice;
            bool skillUi = feature.Has(SkillUiGroup), page = feature.Has(SkillsPageGroup);
            if (!skillUi && !page) return GuidedPanel.None;
            var skills = SkillsPage();
            if (skills == null) return GuidedPanel.None;
            if (skillUi && SkillTree(skills) != null) return GuidedPanel.SkillTree;
            return page ? GuidedPanel.SkillsPage : GuidedPanel.None;
        }
    }
}
