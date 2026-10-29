using EpochAssist.Core;
using EpochAssist.Features.PlannerFormats;

namespace EpochAssist.Features;

internal static partial class Planner
{
    // The build a running request adds its stage to, by id and by the name it had when the request started.
    private static (string Id, string Name)? s_addTo;
    // A link that is already a stage, until the player picks: replace that stage, or keep it (from Add stage from
    // link) or import the link as a build of its own (from a plain paste, which keeps the fetched build).
    private static (string BuildId, int Index, Stage Stage, Build? Fetched)? s_stagePending;
    // A stage name being typed is written once the typing stops.
    private static (string BuildId, float At)? s_stageNameTyped;
    private const float StageNameWriteSeconds = 1f;

    private static readonly ActionButton s_replaceStageButton = TabButton("Replace", "stage.replace", ReplacePendingStage,
        tip: "Replaces that stage with this link's build, keeping its place and name");
    private static readonly ActionButton s_keepStageButton = TabButton("Keep", "stage.keep", KeepPendingStage, tip: "Keeps the stage as it is");
    private static readonly ActionButton s_importNewButton = TabButton("Import as a new build", "stage.importnew", ImportPendingAsNew,
        tip: "Adds this link to the library as a build of its own");

    /// <summary>Asks for the LET planner link of the build's next stage beside its button, filled with the clipboard's
    /// when it holds one; one request once taken.</summary>
    private static ButtonPrompt StageLinkPrompt(string buildId) =>
        new(() => "Last Epoch Tools planner link of the next stage:", "Add stage", text =>
        {
            var paste = PasteKind.Recognise(text);
            if (paste.Kind != PasteSource.LetLink) return "That isn't a Last Epoch Tools planner link.";
            AddStageFrom(buildId, paste);
            return null;
        })
        {
            Input = true,
            Initial = () => PasteKind.Recognise(Clipboard.Text).Kind == PasteSource.LetLink ? Clipboard.Text.Trim() : string.Empty,
        };

    private static void AddStageFrom(string buildId, Paste paste)
    {
        Library.EnsureLoaded();
        s_pending = null;
        s_stagePending = null;
        s_outcome = StartStageFetch(buildId, paste);
        ImportChanged();
    }

    private static string StartStageFetch(string buildId, Paste paste)
    {
        if (Library.Find(buildId) is not { } target) return Words.NotABuild;
        if (paste.Kind != PasteSource.LetLink || !PlannerSites.Shown(PlannerSite.LastEpochTools)) return "That isn't a Last Epoch Tools link.";
        // A request still running keeps its own target.
        if (!Fetch.Start(PlannerSite.LastEpochTools, paste)) return Words.Busy;
        s_failedLetLink = null;
        s_fetching = paste;
        s_addTo = (target.Id, target.Name);
        return Words.Fetching(PlannerSite.LastEpochTools);
    }

    // The fetched build joins its target as the last stage, or asks to replace the stage it already is.
    private static string AddFetched((string Id, string Name) to, Build fetched)
    {
        if (Library.Find(to.Id) is not { } target) return $"{to.Name} was deleted; the stage wasn't added.";
        var stage = Stages.StageFrom(fetched);
        if (Stages.CanAdd(target, stage) is { } refused) return refused;
        int at = Stages.StageIndexOf(target, stage.PlannerId);
        if (at >= 0)
        {
            s_stagePending = (target.Id, at, stage, null);
            return $"That link is stage {at + 1} ({target.Stages[at].Name}). Replace it?";
        }
        if (!Library.AddStages(target.Id, new[] { stage })) return Words.NotSaved(target);
        Stages.RenameIfGuide(target);
        return $"Added \"{stage.Name}\" as stage {target.Stages.Count} of {target.Name}.";
    }

    // A plain paste of a link that is a stage of a saved build offers to replace that stage instead.
    private static string? OfferStageReplace(Build fetched)
    {
        if (fetched.Source != LetFormat.Source || fetched.PlannerId == null) return null;
        foreach (var owner in Library.Builds)
        {
            if (owner.Source != LetFormat.Source || Stages.StageIndexOf(owner, fetched.PlannerId) is not (>= 0 and var at)) continue;
            s_stagePending = (owner.Id, at, Stages.StageFrom(fetched), fetched);
            return $"That link is stage {at + 1} ({owner.Stages[at].Name}) of {owner.Name}. Replace it, or import it as a new build.";
        }
        return null;
    }

    private static void ReplacePendingStage()
    {
        if (s_stagePending is not { } pending) return;
        s_stagePending = null;
        s_outcome = Library.Find(pending.BuildId) is not { } build ? Words.NotABuild
            : Library.ReplaceStage(build.Id, pending.Index, pending.Stage) ? $"Replaced stage {pending.Index + 1} of {build.Name}."
            : Words.NotSaved(build);
        ImportChanged();
    }

    private static void KeepPendingStage()
    {
        if (s_stagePending is not { } pending) return;
        s_stagePending = null;
        s_outcome = $"Kept stage {pending.Index + 1} as it was.";
        ImportChanged();
    }

    private static void ImportPendingAsNew()
    {
        if (s_stagePending is not { Fetched: { } fetched }) return;
        s_stagePending = null;
        s_outcome = Store(fetched);
        ImportChanged();
    }

    /// <summary>Moves another saved build in as the target's next stages; the words say how it went.</summary>
    public static string MergeStages(string fromId, string toId)
    {
        s_outcome = Stages.Merge(fromId, toId);
        ImportChanged();
        return s_outcome;
    }

    // A stage name as it's typed: shown at once, written when the typing stops.
    private static void TypeStageName(string buildId, int index, string name)
    {
        if (Library.RenameStage(buildId, index, name, write: false)) s_stageNameTyped = (buildId, Clock.Now);
    }

    // Run by the tab's banner each frame it shows, and when the feature stops (now true).
    private static void WriteTypedStageName(bool now = false)
    {
        if (s_stageNameTyped is not { } typed || (!now && Clock.Now - typed.At < StageNameWriteSeconds)) return;
        s_stageNameTyped = null;
        Library.WriteNow(typed.BuildId);
    }

    /// <summary>
    /// A LET build's stages in the build view: each named in place, moved up or down and removed (not the last, and
    /// only once asked), and "Add stage from library" listing the saved LET builds of its class. None for other builds,
    /// and while the build has one stage and nothing to add.
    /// </summary>
    private static IEnumerable<MenuRow> StageRows(Build build)
    {
        if (build.Source != LetFormat.Source) yield break;
        var candidates = Stages.MergeCandidates(build);
        if (build.Stages.Count < 2 && candidates.Count == 0) yield break;
        string id = build.Id;
        var names = candidates.Select(b => b.Name).ToList();
        yield return new ListRow("Stages", () => build.Stages, item =>
        {
            var stage = (Stage)item;
            int index = build.Stages.IndexOf(stage);
            // Stages named alike (variants with the same skills) tell apart by their link.
            string? plannerId = stage.PlannerId ?? (index == 0 ? build.PlannerId : null);
            string? url = stage.Url ?? (index == 0 ? build.Url : null);
            return new MenuRow[]
            {
                new ValueTextField("Name", () => stage.Name, name => TypeStageName(id, index, name), 60) { Key = "name", InlineWidth = 340f },
                new ValueText("Level", () => stage.Level > 0 ? $"level {stage.Level}" : string.Empty) { Key = "level", Width = 70f },
                new ValueText("Link", () => plannerId ?? "no link") { Key = "link", Width = 90f },
                TabButton("Open", $"stage.page.{index}", () =>
                {
                    if (url != null) UnityEngine.Application.OpenURL(url);
                }, tip: "Opens this stage's page in the browser", visible: () => url != null, external: true),
            };
        }, "Add stage from library")
        {
            Key = "stages",
            Move = (from, to) => Library.MoveStage(id, from, to - from),
            Remove = index => Library.RemoveStage(id, index),
            // A stage's data has no undo, so it asks once more, beside its button.
            RemovePrompt = index => index < build.Stages.Count
                ? new ButtonPrompt(() => $"Remove stage {index + 1} ({build.Stages[index].Name})? Its loot filter choices go with it.", "Remove", _ =>
                {
                    Library.RemoveStage(id, index);
                    return null;
                })
                : null,
            RemoveBlocked = _ => build.Stages.Count < 2 ? "A build keeps at least one stage" : null,
            AddChoices = candidates.Count > 0 ? () => names : null,
            AddPicked = candidates.Count > 0 ? i =>
            {
                if (i >= 0 && i < candidates.Count) MergeStages(candidates[i].Id, id);
            } : null,
        };
    }

    /// <summary>The page of the stage the character is on (else the build's first), else the build's page.</summary>
    private static string? StagePage(Build build)
    {
        var binding = Library.Binding(Profiles.Key);
        int stage = binding?.Build == build.Id ? binding.Value.Stage : build.FirstStage;
        return (stage >= 0 && stage < build.Stages.Count ? build.Stages[stage].Url : null) ?? build.Url;
    }

    /// <summary>
    /// Last Epoch Tools builds with more than one stage: each stage is one LET build (a guide's variant, say) with its own
    /// planner data, so its gear, idols, blessings and weaver tree are read from it. Stages come from a link or another saved LET
    /// build; neither makes more than the link's own request.
    /// </summary>
    internal static class Stages
    {
        /// <summary>The fetched one-stage LET build as a stage for another build: its trees, its planner data, page and
        /// guide, and its name (the build's, else its skills).</summary>
        public static Stage StageFrom(Build fetched)
        {
            // A copy, so the fetched build can still be imported on its own.
            var stage = Stage.FromJson(fetched.Stages[0].ToJson());
            stage.Raw = fetched.Raw;
            stage.PlannerId = fetched.PlannerId;
            stage.Url = fetched.Url;
            if (stage.GuideSlug == null && MaxrollFormat.Parse(fetched.Raw) is System.Text.Json.Nodes.JsonObject root)
                stage.GuideSlug = Build.Str(root["guide"]) is { Length: > 0 } slug ? slug : null;
            string name = fetched.Name != LetFormat.DefaultName ? fetched.Name : LetSkillsName(fetched) ?? fetched.Name;
            stage.Name = name;
            return stage;
        }

        /// <summary>Null when the stage can join the build; else the words saying why not.</summary>
        public static string? CanAdd(Build target, Stage stage)
        {
            if (target.Source != LetFormat.Source) return $"{target.Name} isn't a Last Epoch Tools build.";
            // Every stage of a build is one character's progression, so one class.
            int classId = target.Stages[0].ClassId;
            return stage.ClassId == classId ? null : Refused(target, ClassName(classId), ClassName(stage.ClassId));
        }

        /// <summary>
        /// Moves a saved LET build in as the target's next stages, with no request: the target is written, then the
        /// characters on the moved build follow it, then its loot filter choices, and only then is it deleted, so a
        /// failure on the way leaves both builds.
        /// </summary>
        public static string Merge(string fromId, string toId)
        {
            if (Library.Find(toId) is not { } target || Library.Find(fromId) is not { } source) return Words.NotABuild;
            if (fromId == toId) return "A build can't be added to itself.";
            if (source.Source != LetFormat.Source) return $"{source.Name} isn't a Last Epoch Tools build.";
            if (CanAdd(target, source.Stages[0]) is { } refused) return refused;
            var moved = new List<Stage>(source.Stages.Count);
            for (int i = 0; i < source.Stages.Count; i++)
            {
                var stage = Stage.FromJson(source.Stages[i].ToJson());
                // The moved build's own data, link and page go with its stages.
                stage.Raw ??= source.Raw;
                if (i == 0)
                {
                    stage.PlannerId ??= source.PlannerId;
                    stage.Url ??= source.Url;
                }
                // A one-stage build's name is its stage's.
                if (source.Stages.Count == 1) stage.Name = source.Name;
                if (StageIndexOf(target, stage.PlannerId) is >= 0 and var at) return $"{source.Name} has stage {at + 1} of {target.Name} already.";
                moved.Add(stage);
            }
            int offset = target.Stages.Count;
            string sourceName = source.Name;
            if (!Library.AddStages(toId, moved)) return Words.NotSaved(target);
            Library.MoveBindings(fromId, toId, offset);
            LootFilterStore.MoveChoices(fromId, toId, offset);
            Library.Delete(fromId);
            RenameIfGuide(target);
            return $"Moved {sourceName} into {target.Name} as stage {offset + 1}.";
        }

        /// <summary>The saved LET builds that can be moved into the target: the same class, not itself.</summary>
        public static List<Build> MergeCandidates(Build target) =>
            target.Source != LetFormat.Source ? new List<Build>()
                : Library.Builds.Where(b => b.Id != target.Id && b.Source == LetFormat.Source && b.Stages[0].ClassId == target.Stages[0].ClassId).ToList();

        /// <summary>The refusal for a link of another class.</summary>
        public static string Refused(Build target, string targetClass, string linkClass) =>
            $"{target.Name} is {A(targetClass)} build; that link is {A(linkClass)} build.";

        // "an Acolyte", "a Mage".
        private static string A(string word) => (word.Length > 0 && "AEIOUaeiou".IndexOf(word[0]) >= 0 ? "an " : "a ") + word;

        /// <summary>The index of the build's stage with this planner id (stage 0 has the build's), or -1.</summary>
        public static int StageIndexOf(Build target, string? plannerId)
        {
            if (string.IsNullOrEmpty(plannerId)) return -1;
            for (int i = 0; i < target.Stages.Count; i++)
                if ((target.Stages[i].PlannerId ?? (i == 0 ? target.PlannerId : null)) == plannerId) return i;
            return -1;
        }

        /// <summary>The guide's title when the first stage names a guide and no other stage names a different one (a
        /// guide's variants name none); else null.</summary>
        public static string? GuideTitleFor(Build build)
        {
            // A guide the player gave counts over what the stages name.
            if (build.GuideSlug != null) return LetFormat.GuideTitle(build.GuideSlug);
            string? slug = build.Stages.Count > 0 ? build.Stages[0].GuideSlug : null;
            if (slug == null) return null;
            foreach (var stage in build.Stages)
                if (stage.GuideSlug != null && stage.GuideSlug != slug) return null;
            return LetFormat.GuideTitle(slug);
        }

        /// <summary>A staged build from one guide takes the guide's title, unless the player named it.</summary>
        public static void RenameIfGuide(Build build)
        {
            if (build.Stages.Count < 2 || build.NamedByPlayer || GuideTitleFor(build) is not { } title) return;
            Library.Rename(build.Id, title);
        }

        // The game's name for the class, or its number before the game's data can be read.
        private static string ClassName(int classId)
        {
            string name = s_run?.Feature is { } feature ? MasteryName(feature, classId, 0) : string.Empty;
            return name.Length == 0 || name.StartsWith("mastery ", StringComparison.Ordinal) ? $"class {classId}" : name;
        }
    }
}
