using EpochAssist.Core;
using EpochAssist.Ui;
using Il2Cpp;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Features;

internal sealed class AutoCastFeature : Feature
{
    public override string Id => "autocast";
    public override string Name => "Autocast";
    public override GuiLayer Layer => GuiLayer.World;
    public override string[] Needs => new[] { "game", "dispatch", "hotkeys", "player", "world" };
    public override FeaturePage? Page => s_page;
    public override string? StatusName => "Autocast";

    private static readonly FeaturePage s_page = new("Autocast", 10, AutoCastPage.Draw) { Switch = AutoCastSettings.Enabled };

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

    /// <summary>The dispatcher's status for autocast while it runs.</summary>
    public override FeatureStatus Status =>
        State == FeatureState.Running && AutoCast.Client is { } client ? Dispatcher.StatusOf(client) : base.Status;

    public override void RegisterSettings() => AutoCastSettings.Register();
    public override void Start() => AutoCast.Start(this);
    public override void Update() => AutoCast.OnUpdate();
    public override void OnGUI() => AutoCast.OnGUI();
    public override void Shutdown() => AutoCast.Shutdown();
}
