using System.Reflection;
using EpochAssist.Core;
using HarmonyLib;
using Il2Cpp;
using Il2CppLE.Data;
using Feature = EpochAssist.Core.Feature;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: logs which game calls the stash window's own buttons make (buy, folder, configure, reorder, parent) while
/// `stash trace on` is set, with their arguments, so the mod can make the same calls. The patches change nothing; each
/// type's methods are their own optional group.
/// </summary>
internal sealed class StashTraceFeature : Feature
{
    /// <summary>The patches log only while this is set.</summary>
    public static bool On;

    public override string Id => "stashtrace";
    public override string Name => "Stash trace";

    public override Type[] PatchClasses => new[]
    {
        typeof(PlayerSyncPatch), typeof(ClientCopyPatch), typeof(ServerCommandsPatch), typeof(ContainersPatch), typeof(ManagerPatch), typeof(LegacyUiPatch),
    };

    public override IReadOnlyDictionary<Type, string> PatchGroups { get; } = new Dictionary<Type, string>
    {
        [typeof(PlayerSyncPatch)] = "trace: stash sends",
        [typeof(ClientCopyPatch)] = "trace: stash client copy",
        [typeof(ServerCommandsPatch)] = "trace: stash server commands",
        [typeof(ContainersPatch)] = "trace: stash containers",
        [typeof(ManagerPatch)] = "trace: stash manager",
        [typeof(LegacyUiPatch)] = "trace: stash legacy controls",
    };

    public override void Shutdown() => On = false;

    private static IEnumerable<MethodBase> Named(Type type, Func<string, bool> pick) =>
        AccessTools.GetDeclaredMethods(type).Where(m => !m.IsAbstract && !m.ContainsGenericParameters && pick(m.Name));

    private static void Trace(MethodBase method, object[]? args)
    {
        if (!On) return;
        try
        {
            var ps = method.GetParameters();
            var parts = new List<string>();
            for (int i = 0; i < ps.Length && args != null && i < args.Length; i++)
                parts.Add($"{ps[i].Name}={Show(args[i])}");
            Log.Info($"trace: {method.DeclaringType?.Name}.{method.Name}({string.Join(", ", parts)})");
        }
        catch (Exception e)
        {
            Log.ErrorThrottled("stashtrace", $"trace: {method.Name} read failed: {e.Message}", 30);
        }
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        StashTab t => $"tab#{t.TabId}",
        StashCategory c => $"folder#{c.CategoryId}",
        StashPriority p => p.HasValue() ? "rules" : "no rules",
        bool or int or float or long or byte => value.ToString() ?? "?",
        Enum => value.ToString() ?? "?",
        _ => value.GetType().Name,
    };

    [HarmonyPatch]
    private static class PlayerSyncPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Named(typeof(PlayerSync), n => n.StartsWith("SendStash", StringComparison.Ordinal) || n.StartsWith("ReceiveStash", StringComparison.Ordinal));
        private static void Prefix(MethodBase __originalMethod, object[] __args) => Trace(__originalMethod, __args);
    }

    // The server's stash sync into the client copy: how long after a send the server's answer arrives.
    [HarmonyPatch]
    private static class ClientCopyPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Named(typeof(ClientStashInfo), n => n == "receiveData");
        private static void Prefix(MethodBase __originalMethod) => Trace(__originalMethod, null);
    }

    [HarmonyPatch]
    private static class ServerCommandsPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Named(typeof(ServerStashCommands), n => n.StartsWith("Server", StringComparison.Ordinal) || n.StartsWith("Purchase", StringComparison.Ordinal));
        private static void Prefix(MethodBase __originalMethod, object[] __args) => Trace(__originalMethod, __args);
    }

    [HarmonyPatch]
    private static class ContainersPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Named(typeof(ItemContainersManager), n => n is "PurchaseNewStashTab" or "AddNewStashTab" or "OrganizeTabsLayout" or "OrganizeCategoriesLayout" or "ReorderForDuplicate" or "SaveStashLayout");
        private static void Prefix(MethodBase __originalMethod, object[] __args) => Trace(__originalMethod, __args);
    }

    [HarmonyPatch]
    private static class ManagerPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Named(typeof(StashManager), n => n.StartsWith("Request", StringComparison.Ordinal) || n.StartsWith("Configure", StringComparison.Ordinal) || n == "NotifyStateUpdated");
        private static void Prefix(MethodBase __originalMethod, object[] __args) => Trace(__originalMethod, __args);
    }

    [HarmonyPatch]
    private static class LegacyUiPatch
    {
        private static IEnumerable<MethodBase> TargetMethods() => Named(typeof(StashTabbedUIControls), n => n.StartsWith("Handle", StringComparison.Ordinal) || n.StartsWith("Set", StringComparison.Ordinal) || n == "MoveTab" || n.StartsWith("OnTab", StringComparison.Ordinal) || n.StartsWith("OnCategory", StringComparison.Ordinal));
        private static void Prefix(MethodBase __originalMethod, object[] __args) => Trace(__originalMethod, __args);
    }
}
