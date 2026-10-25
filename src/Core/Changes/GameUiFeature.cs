using Il2Cpp;
using UnityEngine;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Core;

/// <summary>
/// The core game UI layer: tooltip lines and hiding the game UI. UI presses are the dispatcher's; the prompt
/// kinds they check are here.
/// </summary>
internal sealed class GameUiFeature : Feature
{
    public const string TooltipGroup = "gameui: tooltip lines";
    public const string HideGroup = "gameui: hide";

    public override string Id => "gameui";
    public override string Name => "Game UI";
    public override string[] Needs => new[] { "game" };
    public override Type[] PatchClasses => NestedPatches(typeof(TooltipLines));
    public override IReadOnlyDictionary<Type, string> PatchGroups => PatchClasses.ToDictionary(t => t, _ => TooltipGroup);
    public override string[] DemandGroups => new[] { TooltipGroup };

    public override Requirement[] Requirements => new[]
    {
        Need<UITooltipItem>(nameof(UITooltipItem.loreText), TooltipGroup),
        Need<UITooltipItem>(nameof(UITooltipItem.compareLoreText), TooltipGroup),
        Need<ItemDataUnpacked>(nameof(ItemDataUnpacked.affixes), TooltipGroup),
        Need<UIBase>(nameof(UIBase.instance), HideGroup),
        Need<Canvas>(nameof(Canvas.rootCanvas), HideGroup),
        Need<Canvas>(nameof(Canvas.enabled), HideGroup),
    };

    // Providers registered before this started (it restarted) get the patch back.
    public override void Start()
    {
        if (TooltipLines.Count > 0) FeatureHost.SetPatchGroup(this, TooltipGroup, true);
    }

    public override IEnumerable<string> DiagnosticNotes
    {
        get
        {
            yield return $"tooltip providers {TooltipLines.Count}, patch {(TooltipLines.Patched ? "on" : "off")}";
        }
    }
}
