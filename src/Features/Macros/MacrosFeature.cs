using EpochAssist.Core;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Features;

internal sealed class MacrosFeature : Feature
{
    public override string Id => "macros";
    public override string Name => "Macros";
    public override GuiLayer Layer => GuiLayer.World;
    public override string[] Needs => new[] { "game", "dispatch", "hotkeys", "player", "world" };
    // Reads its settings each frame, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override MenuEntry? Menu => Macros.Entry;
    public override MenuEntry? ConfigMenu => MacroSettingsPage.ConfigEntry;
    public override string? StatusName => "Macros";
    // Slot macros alone average 0.28–0.35 ms; five full slot lists and 50 library macros stay within 1.0 ms (SC-008).
    public override double BudgetMs => 1.0;

    // The skill bar, cooldowns, form and enemies come from the models; sending, channel stops and retargeting are the
    // dispatcher's requirements. What is left is read here: the bar icons it tints and the cursor point it aims at.
    public override PartNeed[] Parts => new[]
    {
        Part("player", "skill bar"),
        Part("player", "cooldown ready"),
        Part("player", "form"),
        Part("world", "enemies"),
    };

    public override Requirement[] Requirements => new[]
    {
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.all)),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.abilityNumber)),
        Need<AbilityBarIcon>(nameof(AbilityBarIcon.icon)),
        Need<MouseManager>(nameof(MouseManager.AbilityUseMousePoint)),
    };

    /// <summary>The slot macros' status from the dispatcher while it runs, or a library run's.</summary>
    public override FeatureStatus Status =>
        State == FeatureState.Running && SlotMacros.Client is { } client ? SlotMacros.StatusOf(client) : base.Status;

    public override void RegisterSettings() => MacroSettings.Register();
    public override void Start() => SlotMacros.Start(this);
    public override void Update() => SlotMacros.OnUpdate();
    public override void OnGUI() => SlotMacros.OnGUI();
    public override void Shutdown() => SlotMacros.Shutdown();
}
