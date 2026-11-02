using EpochAssist.Core;
using EpochAssist.Ui;
using UnityEngine;
using S = EpochAssist.Features.StashSettings;

namespace EpochAssist.Features;

/// <summary>
/// Stash search: keeps facts for every stash item (place, fields, every stat with its numbers), checks a search against
/// them in slices, and shows the results on the mod's page and beside the game's stash window. Only reads: no item is
/// moved, stored, deleted or changed, and tabs switch only through the game's own buttons.
/// </summary>
internal static partial class StashSearch
{
    // Each way into the game in its own group, so a game change takes out only what it breaks. Reading items is required.
    public const string ReadGroup = "search read";
    public const string ValuesGroup = "search values";
    public const string PanelGroup = "search panel";
    public const string TooltipGroup = "search tooltip";
    public const string GoGroup = "search go";
    public const string BoxGroup = "search box";
    public const string IconsGroup = "search icons";
    public const string SyntaxGroup = "search syntax";
    public const string CountsGroup = "search affix counts";

    public const string PageBlock = "stashsearch.page";

    private const float PollSeconds = 0.5f;

    private static Feature? s_feature;
    private static float s_nextPoll;
    private static int s_drawnFrame = -10;

    /// <summary>Whether an optional group is there (true before the feature starts).</summary>
    public static bool Has(string group) => s_feature?.Has(group) ?? true;

    /// <summary>Items wait to be read or a search is being checked: the frame's budget allows the slices.</summary>
    public static bool Busy => FactsQueued > 0 || Matching || BoxMatching;

    /// <summary>Whether the Search page drew this frame or the last one.</summary>
    public static bool PageShown => Time.frameCount - s_drawnFrame <= 1;

    public static void Start(Feature feature)
    {
        s_feature = feature;
        Reset();
        SweepPanels();
        GameIcons.Sweep();
        ItemTooltip.Hook();
        MenuBlocks.Register(PageBlock, (rect, _) => DrawPage(rect), _ => PageBridgeLines());
        // Unbound by default; players bind it on the hotkeys page.
        Actions.Register(feature.Id, "stash.search", "Stash search", TogglePage);
    }

    /// <summary>Forgets everything a run keeps.</summary>
    public static void Reset()
    {
        s_nextPoll = 0f;
        s_drawnFrame = -10;
        ResetFacts();
        ResetMatch();
        ResetCatalogue();
        ResetPage();
        ResetSaved();
        s_boxLive = null;
        s_boxLast = null;
        SetBoxQuery(null, null);
        s_boxChecking = false;
    }

    public static void Shutdown()
    {
        // The game's box gets its text back and the tooltip closes before the panel goes.
        ResetHighlight(restore: true);
        ItemTooltip.Close();
        ItemTooltip.Unhook();
        DestroyPanel();
        Reset();
        GameIcons.Free();
        SceneObjects.Forget();
        s_feature = null;
    }

    // A zone change reads the stash again; the filter being edited, the saved and recent ones stay, and it runs again.
    public static void OnSceneChanged()
    {
        ResetHighlight(restore: false);
        ItemTooltip.Close();
        DestroyPanel();
        SceneObjects.Forget();
        var keep = s_active;
        s_nextPoll = 0f;
        ResetFacts();
        ResetMatch();
        ResetCatalogue();
        s_boxLive = null;
        s_boxChecking = false;
        SetBoxQuery(null, null);
        if (keep != null && !keep.IsEmpty) SetActive(keep);
    }

    /// <summary>Lists the stash twice a second, then reads queued items and checks the search, each in a slice.</summary>
    public static void Frame()
    {
        if (!S.SearchEnabled.Value)
        {
            if (s_highlightOn) ResetHighlight(restore: true);
            DestroyPanel();
            return;
        }
        if (Time.unscaledTime >= s_nextPoll)
        {
            s_nextPoll = Time.unscaledTime + PollSeconds;
            Poll();
        }
        FactsSlice();
        MatchSlice();
        BoxSlice();
        GameIcons.Poll();
        PanelFrame();
        HighlightFrame();
        RecentFrame();
        if (!PageShown) ClosePageTooltip();
        ItemTooltip.Frame();
    }

    /// <summary>Off, ready, searching, or the stash's item and match counts.</summary>
    public static FeatureStatus StatusOf()
    {
        if (!S.SearchEnabled.Value) return FeatureStatus.Off("Off");
        if (FactsCount == 0 && FactsQueued == 0) return new FeatureStatus(StatusState.Active, "Ready", null);
        if (Matching || s_passNew > 0) return new FeatureStatus(StatusState.Active, "Searching…", null);
        int matches = Active == null ? 0 : ResultCount;
        return new FeatureStatus(StatusState.Active, Active == null ? $"{FactsCount} items" : $"{FactsCount} items, {matches} {(matches == 1 ? "match" : "matches")}", null);
    }

    private static void MarkDrawn() => s_drawnFrame = Time.frameCount;
}
