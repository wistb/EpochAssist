using EpochAssist.Core;

namespace EpochAssist.Dev;

// Requires things the game doesn't have, so `compat` shows that missing members and patch targets are caught.
internal sealed class CompatProbeFeature : Feature
{
    public override string Id => "selftest";
    public override string Name => "Compat self-test";
    public override Requirement[] Requirements => new[]
    {
        Need<Il2Cpp.CameraManager>("noSuchMember"),
        Need<Il2Cpp.CameraManager>(nameof(Il2Cpp.CameraManager.targetZoom), "interop field"),
        Need<Il2Cpp.CameraManager>("noSuchField", "optional"),
    };
    public override Type[] PatchClasses => new[] { typeof(MissingTargetPatch), typeof(MissingOptionalPatch) };
    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } =
        new Dictionary<Type, string> { [typeof(MissingOptionalPatch)] = "optional" };

    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.CameraManager), "NoSuchMethod")]
    private static class MissingTargetPatch
    {
        private static void Postfix() { }
    }

    [HarmonyLib.HarmonyPatch(typeof(Il2Cpp.CameraManager), "NoSuchOptionalMethod")]
    private static class MissingOptionalPatch
    {
        private static void Postfix() { }
    }
}
