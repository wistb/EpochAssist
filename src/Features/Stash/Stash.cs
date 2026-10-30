using EpochAssist.Core;
using UnityEngine;
using S = EpochAssist.Features.StashSettings;

namespace EpochAssist.Features;

/// <summary>
/// Stash layouts: reads the season stash into a plain snapshot, plans a preset against it at a gold budget, and builds
/// the difference one step per press through the code the game's own stash buttons run. Nothing is deleted, no item is
/// moved, and gold is spent only on a press whose button showed the price.
/// </summary>
internal static partial class Stash
{
    // Each way into the game in its own group, so a game change takes out only what it breaks.
    public const string ReadGroup = "stash read";
    public const string BuyGroup = "stash buy";
    public const string ConfigureGroup = "stash configure";
    public const string FolderGroup = "stash folder";
    public const string MoveGroup = "stash move";
    public const string TestGroup = "stash test";
    public const string IconsGroup = "stash icons";

    public const string PageBlock = "stash.page";


    private const float PollSeconds = 0.5f;

    private static Feature? s_feature;
    // The frame the page block last drew; the page counts as shown for one frame after it.
    private static int s_drawnFrame = -10;
    private static float s_nextRead;
    private static StashSnapshot? s_snap;
    private static Choices? s_choices;
    private static StashKey? s_choicesKey;
    private static Preview? s_preview;
    private static (ulong Fingerprint, string Preset, ulong Choices, int Uniques) s_planned;

    /// <summary>Whether the Stash page drew this frame or the last one.</summary>
    public static bool PageShown => Time.frameCount - s_drawnFrame <= 1;

    /// <summary>Whether an optional group is there (true before the feature starts, for the planner on plain data).</summary>
    public static bool Has(string group) => s_feature?.Has(group) ?? true;

    /// <summary>The last read; null without a stash.</summary>
    public static StashSnapshot? Snapshot => s_snap;

    /// <summary>The preview for the last read and the current choices.</summary>
    public static Preview? CurrentPreview => s_preview;

    /// <summary>The current stash's choices (defaults before a stash is read).</summary>
    public static Choices CurrentChoices => s_choices ??= new Choices();

    public static Preset CurrentPreset => Presets.Find(CurrentChoices.Preset) ?? Presets.Default;

    public static void Start(Feature feature)
    {
        s_feature = feature;
        Reset();
        MenuBlocksRegister();
    }

    /// <summary>Forgets everything a run keeps.</summary>
    public static void Reset()
    {
        s_drawnFrame = -10;
        s_nextRead = 0f;
        s_snap = null;
        s_choices = null;
        s_choicesKey = null;
        s_preview = null;
        s_planned = default;
        ResetSteps();
        ResetPage();
    }

    public static void Shutdown()
    {
        StopAll("the mod stopped");
        Reset();
        ReleaseIcons();
        s_feature = null;
    }

    public static void OnSceneChanged()
    {
        StopAll("the scene changed");
        s_snap = null;
        s_preview = null;
    }

    /// <summary>Reads while the page shows (or a step waits), at most twice a second, and plans again when an input changed.</summary>
    public static void Frame()
    {
        if (!S.Enabled.Value) return;
        FolderWindowFrame();
        bool sending = StepsBusy;
        if (!PageShown && !sending)
        {
            StopAllIfRunning("the page closed");
            return;
        }
        if (PageShown) BelongsFrame();
        if (Time.unscaledTime < s_nextRead) return;
        // While a step waits for the game's answer, every frame, so it shows at once; while Apply all waits out a message
        // limit, nothing until the wait ends; else twice a second.
        s_nextRead = State switch
        {
            ApplyState.Sending => Time.unscaledTime,
            ApplyState.Paused => Math.Max(Time.unscaledTime, s_resumeAt),
            _ => Time.unscaledTime + PollSeconds,
        };
        Refresh();
        StepsFrame();
    }

    /// <summary>Reads the stash now and plans again if anything the plan depends on changed.</summary>
    public static void Refresh(bool force = false)
    {
        s_snap = Read();
        if (s_snap == null)
        {
            s_preview = null;
            return;
        }
        if (s_choicesKey != s_snap.Key)
        {
            s_choicesKey = s_snap.Key;
            s_choices = ChoicesFile.Load(s_snap.Key, s_snap);
        }
        // A choice whose tab or folder now has another name (the game renumbers after a delete) goes back to its default;
        // while a step waits its own rename isn't taken for one.
        if (!StepsBusy && CurrentChoices.Validate(s_snap)) ChoicesFile.Save(s_snap.Key, CurrentChoices);
        var key = (s_snap.Fingerprint, CurrentPreset.Id, CurrentChoices.Hash(), UniquesGeneration());
        if (!force && s_preview != null && key == s_planned) return;
        s_planned = key;
        s_preview = PlanNow(s_snap, CurrentPreset, CurrentChoices, CurrentChoices.Budget);
        InvalidateRows();
    }

    /// <summary>Saves the current choices and plans again.</summary>
    public static void ChoicesChanged()
    {
        if (s_snap != null)
        {
            CurrentChoices.RememberNames(s_snap);
            ChoicesFile.Save(s_snap.Key, CurrentChoices);
        }
        Refresh(force: true);
    }

    /// <summary>The preview of a preset at a budget against a snapshot, with the game's prices and limits.</summary>
    public static Preview PlanNow(StashSnapshot snap, Preset preset, Choices choices, long budget)
    {
        var preview = Plan(InputFor(snap, preset, choices, budget));
        CheckOrder(snap, preview);
        return preview;
    }

    public static PlanInput InputFor(StashSnapshot snap, Preset preset, Choices choices, long budget) => new()
    {
        Snapshot = snap, Preset = preset, Choices = choices, Budget = budget, Price = Price, MaxTabs = MaxTabs, MaxFolders = MaxFolders,
        Fill = choices.FullLayout && Has(TestGroup) ? FillCounts(snap, preset) : null,
        RulesOf = RulesOf,
    };

    /// <summary>The feature's status: off, waiting for a stash, or the stash's size.</summary>
    public static FeatureStatus StatusOf(Feature feature)
    {
        if (!S.Enabled.Value) return FeatureStatus.Off("Off");
        if (StepsBusy) return new FeatureStatus(StatusState.Active, StepsStatus, null);
        return new FeatureStatus(StatusState.Active, s_snap == null ? "Ready" : $"{s_snap.Folders.Count} folders, {s_snap.TabCount} tabs", null);
    }

    private static void MarkDrawn() => s_drawnFrame = Time.frameCount;
}
