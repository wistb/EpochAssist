using System.Reflection;
using EpochAssist.Core;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace EpochAssist.Dev;

// Type names as typed in the inspection commands, and the live objects of a type. Read-only: lookups and
// FindObjectsOfTypeAll only.
internal static class GameTypes
{
    public sealed record Lookup(Type? Type, string? Error)
    {
        public bool IsUnityObject => Type != null && typeof(UnityEngine.Object).IsAssignableFrom(Type);
    }

    // Path and the cast to the type are made only when asked for: a common type can have 100,000 live objects.
    public sealed record Instance(int Index, float? Distance, bool Active, Lazy<string> Path, Lazy<Il2CppObjectBase?> Object);

    private static readonly Dictionary<Type, MethodInfo> s_casts = new();

    /// <summary>
    /// The exact full name in any loaded assembly, then Il2Cpp.&lt;name&gt;, then the simple name across the game's
    /// interop assemblies and Unity's (exact case first). More than one match at a step is an error listing them.
    /// </summary>
    public static Lookup Resolve(string name)
    {
        if (name.Length == 0) return new Lookup(null, "no type given");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (string full in new[] { name, "Il2Cpp." + name })
        {
            var exact = assemblies.Select(a => a.GetType(full)).Where(t => t != null).Distinct().ToList();
            if (exact.Count > 0) return Pick(name, exact!);
        }
        var types = assemblies.Where(a => a.GetName().Name is { } n && (n.StartsWith("Il2Cpp") || n.StartsWith("UnityEngine")))
            .SelectMany(TypesOf).Where(t => !t.IsGenericTypeDefinition).ToList();
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var simple = types.Where(t => t.Name.Equals(name, comparison)).Distinct().ToList();
            if (simple.Count > 0) return Pick(name, simple);
        }
        return new Lookup(null, $"no type {name}");
    }

    /// <summary>The managed interop type behind an Il2Cpp class, or null.</summary>
    public static Type? Managed(Il2CppSystem.Type type)
    {
        string full = type.FullName ?? type.Name;
        foreach (string candidate in new[] { full, "Il2Cpp." + full, "Il2Cpp" + full })
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetType(candidate) is { } t) return t;
            }
        }
        return null;
    }

    /// <summary>The object as its own Il2Cpp class's interop type when its wrapper is a base or an interface.</summary>
    public static object Specific(object value)
    {
        if (value is not Il2CppSystem.Object obj || !Game.Alive(obj)) return value;
        try
        {
            var type = Managed(obj.GetIl2CppType());
            // The wrapper can be an interface's (ItemDataProvider), which isn't a base of the class; TryCast checks
            // the Il2Cpp class either way.
            return type != null && type != value.GetType() && !type.IsAssignableFrom(value.GetType()) ? Cast(obj, type) ?? value : value;
        }
        catch (Exception)
        {
            return value;
        }
    }

    /// <summary>TryCast to a type known only at run time.</summary>
    public static Il2CppObjectBase? Cast(Il2CppObjectBase obj, Type type)
    {
        if (!s_casts.TryGetValue(type, out var cast))
        {
            cast = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.TryCast))!.MakeGenericMethod(type);
            s_casts[type] = cast;
        }
        return cast.Invoke(obj, null) as Il2CppObjectBase;
    }

    /// <summary>
    /// The type's objects in loaded scenes (prefabs and assets left out), nearest the player first, or by hierarchy
    /// path with no player. Inactive objects are included and marked.
    /// </summary>
    public static (List<Instance>? List, string? Error) Instances(Lookup lookup)
    {
        var type = lookup.Type!;
        if (!lookup.IsUnityObject) return (null, $"{type.Name} is not a Unity object (reach it through inspect or statics)");
        Vector3? from = Player.Transform is Transform t ? t.position : null;
        var found = new List<(float? Distance, bool Active, Lazy<Il2CppObjectBase?> Obj, Lazy<string> Path)>();
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.From(type)))
        {
            if (!Game.Alive(o)) continue;
            GameObject? go = o.TryCast<Component>()?.gameObject ?? o.TryCast<GameObject>();
            if (go == null || !go.scene.isLoaded) continue;
            float? distance = from is Vector3 p ? Vector3.Distance(p, go.transform.position) : null;
            found.Add((distance, go.activeInHierarchy, new Lazy<Il2CppObjectBase?>(() => Cast(o, type)), new Lazy<string>(() => PathOf(go))));
        }
        found.Sort((a, b) =>
        {
            int byDistance = (a.Distance ?? 0f).CompareTo(b.Distance ?? 0f);
            return byDistance != 0 ? byDistance : string.CompareOrdinal(a.Path.Value, b.Path.Value);
        });
        return (found.Select((f, i) => new Instance(i, f.Distance, f.Active, f.Path, f.Obj)).ToList(), null);
    }

    /// <summary>scene/root/…/name.</summary>
    public static string PathOf(GameObject go)
    {
        string path = go.name;
        for (var p = go.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return go.scene.name + "/" + path;
    }

    private static Lookup Pick(string name, List<Type> matches) => matches.Count == 1
        ? new Lookup(matches[0], null)
        : new Lookup(null, $"{name} is ambiguous: {string.Join(", ", matches.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal))}");

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.Where(t => t != null)!;
        }
    }
}
