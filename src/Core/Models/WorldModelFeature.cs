namespace EpochAssist.Core;

internal sealed class WorldModelFeature : Feature
{
    public override string Id => "world";
    public override string Name => "World model";
    public override string[] Needs => new[] { "game", "player" };
    // The whole feature's, not its RefreshSliceMs: discovery and the refresh average about 0.4 ms in combat.
    public override double BudgetMs => 1.0;

    // One group per part, or one per mode where the source differs.
    public override Requirement[] Requirements => WorldSources.Requirements;

    public override void Start() => World.Start(this);
    public override void Update() => World.Update();
    public override void OnSceneChanged() => World.Clear();
    public override void Shutdown() => World.Stop();
}
