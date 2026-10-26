using EpochAssist.Core;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Features;

internal sealed class AutoCastFeature : Feature
{
    public override string Id => "autocast";
    public override string Name => "Macros";
    public override GuiLayer Layer => GuiLayer.World;
    public override string[] Needs => new[] { "game", "dispatch", "hotkeys", "player", "world" };
    // Reads its settings each frame, after the character's values are in place.
    public override string[] RunsAfter => new[] { "profiles" };
    public override MenuEntry? Menu => Macros.Entry;
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
        State == FeatureState.Running && AutoCast.Client is { } client ? AutoCast.StatusOf(client) : base.Status;

    public override void RegisterSettings() => AutoCastSettings.Register();
    public override void Start() => AutoCast.Start(this);
    public override void Update() => AutoCast.OnUpdate();
    public override void OnGUI() => AutoCast.OnGUI();
    public override void Shutdown() => AutoCast.Shutdown();
}
