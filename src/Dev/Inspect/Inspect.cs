using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using EpochAssist.Core;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace EpochAssist.Dev;

// inspect, statics and the targets and member paths that watch shares: reflection over the interop types, read-only.
// Every value is read on its own, so one failing getter shows as "? (Exception)" and the rest still answer.
internal static class Inspect
{
    /// <summary>One path step: name, name[i] or name[*].</summary>
    public sealed record Step(string Name, int? Index, bool All)
    {
        public override string ToString() => Name + (All ? "[*]" : Index is int i ? $"[{i}]" : string.Empty);
    }

    // A type with no singleton: its statics are the path's first step (MonsterRarityManager monsterModByKey[*]).
    private sealed record StaticsOf(Type Type);

    // A value reached by a path, with the member it was read from (null for the target itself).
    private sealed record Node(object? Value, string Name, Type DeclaredType);

    private const BindingFlags InstanceMembers = BindingFlags.Public | BindingFlags.Instance;
    private const int MaxString = 120;

    // Listing stops at these bases; their members are plumbing or the same on every object.
    private static readonly HashSet<string> s_stopAt = new()
    {
        "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "Il2CppSystem.Object", "Il2CppSystem.ValueType", "System.Object",
        "UnityEngine.MonoBehaviour", "UnityEngine.Behaviour", "UnityEngine.Component", "UnityEngine.Object",
    };

    // The members from those bases that are worth listing.
    private static readonly string[] s_baseMembers = { "name", "enabled", "activeSelf", "activeInHierarchy" };

    private static readonly HashSet<string> s_plumbing = new() { "ObjectClass", "Pointer", "WasCollected", "NativeClassPtr" };

    // Getters that change the game when read: Unity makes a per-object copy of the material or mesh on first read
    // (and TextMesh Pro of the font material), which would add objects and change how the thing renders. They are never
    // read; the shared form beside each is.
    private static readonly Dictionary<string, string> s_denied = new()
    {
        ["UnityEngine.Renderer.material"] = "sharedMaterial",
        ["UnityEngine.Renderer.materials"] = "sharedMaterials",
        ["UnityEngine.MeshFilter.mesh"] = "sharedMesh",
        ["UnityEngine.Collider.material"] = "sharedMaterial",
        ["Il2CppTMPro.TMP_Text.fontMaterial"] = "fontSharedMaterial",
        ["Il2CppTMPro.TMP_Text.fontMaterials"] = "fontSharedMaterials",
    };

    private static readonly Regex s_step = new(@"^(?<name>[^\[\]]+)(\[(?<index>\*|-?\d+)\])?$");

    // ---- Commands ----

    /// <summary>inspect &lt;target&gt; [member.path]</summary>
    public static string Run(string target, string path)
    {
        var (start, error) = Target(target);
        if (error != null) return $"error inspect {target}: {error}";
        if (!ParsePath(path, out var steps, out error)) return $"error inspect {target}: {error}";
        if (start is StaticsOf none && steps.Count == 0) return $"error inspect {target}: {NoSingleton(none.Type)}";

        string title = $"inspect {target}{(path.Length > 0 ? " " + path : string.Empty)}";
        var lines = new List<string>();
        int all = steps.FindIndex(s => s.All);
        var (node, stepError) = Walk(new Node(start, target, start!.GetType()), steps, 0, all < 0 ? steps.Count : all);
        if (stepError != null) return $"error {title}: {stepError}";

        if (all >= 0)
        {
            var (items, more, itemsError) = Items(node!, steps, all);
            if (itemsError != null) return $"error {title}: {itemsError}";
            lines.Add($"ok {title}: {items!.Count + more} items");
            // The rest of the path is read from each item: buffs[*].remainingDuration.
            bool rest = all + 1 < steps.Count;
            foreach (var (label, item) in items)
            {
                var (end, endError) = Walk(new Node(item, label, item?.GetType() ?? typeof(object)), steps, all + 1, steps.Count);
                lines.Add(endError != null ? $"{label} {endError}" : rest ? $"{label} {ValueLine(end!)}" : ValueLine(end!));
            }
            if (more > 0) lines.Add($"… {more} more");
            return DevOutput.Reply("inspect", lines);
        }

        object? value = node!.Value;
        if (steps.Count > 0 && (IsPlain(value) || CountOf(value!) != null))
        {
            return $"ok {title}: {ValueLine(node)}" + (CountOf(value!) != null ? $" (read {steps[^1]}[*] for the items)" : string.Empty);
        }
        var members = Members(value!.GetType(), statics: false);
        lines.Add($"ok {title}: {Describe(value)} ({Math.Min(members.Count, DevOutput.MaxMembers)} members)");
        AddMembers(lines, value, members);
        return DevOutput.Reply("inspect", lines);
    }

    /// <summary>statics &lt;Type&gt;</summary>
    public static string Statics(string typeName)
    {
        var lookup = GameTypes.Resolve(typeName);
        if (lookup.Type is not { } type) return $"error statics: {lookup.Error}";
        var members = Members(type, statics: true);
        var lines = new List<string> { $"ok statics {type.FullName}: {Math.Min(members.Count, DevOutput.MaxMembers)} members" };
        AddMembers(lines, null, members);
        return DevOutput.Reply("statics", lines);
    }

    // ---- Targets and paths (shared with watch) ----

    /// <summary>player | &lt;Type&gt; | &lt;Type&gt;#n | hovered | hovered:item, resolved now.</summary>
    public static (object? Value, string? Error) Target(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "player":
                return Player.Object is { } player ? (player, null) : (null, $"no player (scene {GameState.ActiveSceneName})");
            case "hovered":
            case "hovered:item":
                if (!Hovered.HasRun) return (null, "run hovered first");
                Il2CppObjectBase? last = text.Length > "hovered".Length ? Hovered.LastItem : Hovered.LastHit;
                if (last == null) return (null, text.Length > "hovered".Length ? "the last hovered found no item" : "the last hovered found no game UI");
                return Game.Alive(last) ? (GameTypes.Specific(last), null) : (null, "target gone");
        }

        int hash = text.LastIndexOf('#');
        var lookup = GameTypes.Resolve(hash >= 0 ? text[..hash] : text);
        if (lookup.Type is not { } type) return (null, lookup.Error);
        if (hash >= 0)
        {
            if (!int.TryParse(text[(hash + 1)..], out int n) || n < 0) return (null, $"bad index in {text}");
            var (list, error) = GameTypes.Instances(lookup);
            if (list == null) return (null, error);
            if (n >= list.Count) return (null, $"no {type.Name}#{n} ({list.Count} live)");
            return list[n].Object.Value is { } live && Game.Alive(live) ? (GameTypes.Specific(live), null) : (null, "target gone");
        }

        // The singleton: a static of the type's own type, "instance" first, then "main" (Camera.main).
        var singleton = Members(type, statics: true)
            .Where(m => type.IsAssignableFrom(MemberType(m)))
            .OrderBy(m => m.Name.Equals("instance", StringComparison.OrdinalIgnoreCase) ? 0 : m.Name.Equals("main", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .FirstOrDefault();
        if (singleton == null) return (new StaticsOf(type), null);
        var (value, failure) = Read(singleton, null);
        if (failure != null) return (null, $"{type.Name}.{singleton.Name}: {failure}");
        return value is Il2CppObjectBase obj && Game.Alive(obj) ? (GameTypes.Specific(obj), null) : (null, "target gone");
    }

    /// <summary>Steps separated by '.', each name, name[i] or name[*]; an empty path has no steps.</summary>
    public static bool ParsePath(string path, out List<Step> steps, out string? error)
    {
        steps = new List<Step>();
        error = null;
        if (path.Length == 0) return true;
        foreach (string part in path.Split('.'))
        {
            var m = s_step.Match(part);
            if (!m.Success)
            {
                error = $"bad path step '{part}' (name, name[i] or name[*])";
                return false;
            }
            string index = m.Groups["index"].Value;
            steps.Add(new Step(m.Groups["name"].Value, index is "" or "*" ? null : int.Parse(index, CultureInfo.InvariantCulture), index == "*"));
        }
        return true;
    }

    /// <summary>
    /// The object that owns the path's last member, resolved once (watch binds it), or the error at the step that stopped.
    /// </summary>
    public static (object? Owner, string? Error) Owner(object start, List<Step> steps)
    {
        var (node, error) = Walk(new Node(start, string.Empty, start.GetType()), steps, 0, steps.Count - 1);
        if (error != null) return (null, error);
        return node!.Value == null ? (null, $"step {steps.Count - 1} '{steps[^2]}': null") : (node.Value, null);
    }

    /// <summary>The last step read from its owner, as a value line's value, or null with the error.</summary>
    public static string? ReadLast(object owner, List<Step> steps, out string? error)
    {
        var (node, stepError) = Walk(new Node(owner, string.Empty, owner.GetType()), steps, steps.Count - 1, steps.Count);
        error = stepError;
        return stepError == null ? Format(node!.Value) : null;
    }

    // Steps [from, to) from the node; the error names the step that stopped the walk (counted from 1).
    private static (Node? Node, string? Error) Walk(Node node, List<Step> steps, int from, int to)
    {
        for (int k = from; k < to; k++)
        {
            var step = steps[k];
            string at = $"step {k + 1} '{step}'";
            if (node.Value == null) return (null, k == 0 ? "target gone" : $"step {k} '{steps[k - 1]}': null");

            object current = node.Value;
            var statics = current as StaticsOf;
            var member = statics != null ? FindStatic(statics.Type, step.Name) : FindMember(current.GetType(), step.Name);
            object? value;
            Type declared;
            if (member != null)
            {
                var (read, failure) = Read(member, statics != null ? null : current);
                if (failure != null) return (null, $"{at}: {failure}");
                value = read;
                declared = MemberType(member);
            }
            else if (ComponentOf(current, step.Name) is { } component)
            {
                value = component;
                declared = component.GetType();
            }
            else
            {
                return (null, $"{at}: missing");
            }

            value = value == null ? null : GameTypes.Specific(value);
            if (step.Index is int i)
            {
                if (value == null) return (null, $"{at}: null");
                // A dictionary with int keys is indexed by key (monsterModByKey[1150886571]).
                if (IsDictionary(value.GetType()) && KeyIndexer(value.GetType()) is { } byKey)
                {
                    try
                    {
                        value = byKey.GetValue(value, new object[] { i });
                    }
                    catch (Exception)
                    {
                        return (null, $"{at}: no key {i}");
                    }
                    value = value == null ? null : GameTypes.Specific(value);
                    declared = value?.GetType() ?? typeof(object);
                    node = new Node(value, step.ToString(), declared);
                    continue;
                }
                if (CountOf(value) is not int count) return (null, $"{at}: not a collection ({TypeName(value.GetType())})");
                if (i >= count) return (null, $"{at}: index out of range (count {count})");
                var item = ItemsOf(value, i + 1).Skip(i).FirstOrDefault();
                value = item.Value == null ? null : GameTypes.Specific(item.Value);
                declared = value?.GetType() ?? typeof(object);
            }
            node = new Node(value, step.ToString(), declared);
        }
        return (node, null);
    }

    // The items for a [*] step, up to MaxItems, with how many more there are.
    private static (List<(string Label, object? Value)>? Items, int More, string? Error) Items(Node node, List<Step> steps, int all)
    {
        // The walk stopped before the [*] step; its member is read here as a plain step.
        var plain = steps.ToList();
        plain[all] = steps[all] with { All = false };
        var (owner, error) = Walk(node, plain, all, all + 1);
        if (error != null) return (null, 0, error);
        string at = $"step {all + 1} '{steps[all]}'";
        if (owner!.Value is not { } value) return (null, 0, $"{at}: null");
        if (CountOf(value) is not int count) return (null, 0, $"{at}: not a collection ({TypeName(value.GetType())})");
        var items = ItemsOf(value, DevOutput.MaxItems).Select(x => (x.Label, x.Value == null ? null : GameTypes.Specific(x.Value))).ToList();
        return (items, Math.Max(0, count - items.Count), null);
    }

    // A component on the GameObject (or the component's GameObject) whose class name matches, exact case first.
    private static object? ComponentOf(object current, string name)
    {
        GameObject? go = current as GameObject ?? (current as Component)?.gameObject;
        if (go == null) return null;
        var components = go.GetComponents<Component>().Where(c => c != null).ToList();
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var match = components.FirstOrDefault(c => c.GetIl2CppType() is { } t && (t.Name.Equals(name, comparison) || (t.FullName?.Equals(name, comparison) ?? false)));
            if (match != null) return GameTypes.Specific(match);
        }
        return null;
    }

    // ---- Members ----

    // Instance members from the type up to the listing's stop bases, plus the few worth listing from them; or the
    // type's own statics. Interop plumbing and indexed properties are skipped.
    private static List<MemberInfo> Members(Type type, bool statics)
    {
        var found = new Dictionary<string, MemberInfo>();
        void Add(IEnumerable<MemberInfo> members)
        {
            foreach (var m in members)
            {
                if (m is PropertyInfo { CanRead: false } || m is PropertyInfo p && p.GetIndexParameters().Length > 0) continue;
                if (m.Name.StartsWith("NativeFieldInfoPtr_") || m.Name.StartsWith("NativeMethodInfoPtr_") || s_plumbing.Contains(m.Name)) continue;
                found.TryAdd(m.Name, m);
            }
        }

        if (statics)
        {
            const BindingFlags own = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Add(type.GetProperties(own));
            Add(type.GetFields(own));
        }
        else
        {
            for (var t = type; t != null && !s_stopAt.Contains(t.FullName ?? string.Empty); t = t.BaseType)
            {
                Add(t.GetProperties(InstanceMembers | BindingFlags.DeclaredOnly));
                Add(t.GetFields(InstanceMembers | BindingFlags.DeclaredOnly));
            }
            Add(s_baseMembers.Select(n => (MemberInfo?)type.GetProperty(n, InstanceMembers)).Where(m => m != null)!);
        }
        return found.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddMembers(List<string> lines, object? obj, List<MemberInfo> members)
    {
        if (obj is GameObject go) lines.Add("components: " + string.Join(", ", go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetIl2CppType().Name)));
        foreach (var m in members.Take(DevOutput.MaxMembers))
        {
            var (value, failure) = Read(m, obj);
            lines.Add($"{m.Name}: {TypeName(MemberType(m))} = {failure ?? Format(value == null ? null : GameTypes.Specific(value))}");
        }
        if (members.Count > DevOutput.MaxMembers) lines.Add($"… {members.Count - DevOutput.MaxMembers} more members");
    }

    // A readable field or parameterless property, exact name first, then ignoring case.
    private static MemberInfo? FindMember(Type type, string name)
    {
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var member = type.GetProperties(InstanceMembers).FirstOrDefault(p => p.Name.Equals(name, comparison) && p.CanRead && p.GetIndexParameters().Length == 0)
                ?? (MemberInfo?)type.GetFields(InstanceMembers).FirstOrDefault(f => f.Name.Equals(name, comparison));
            if (member != null && !s_plumbing.Contains(member.Name)) return member;
        }
        return null;
    }

    private static MemberInfo? FindStatic(Type type, string name)
    {
        var statics = Members(type, statics: true);
        return statics.FirstOrDefault(m => m.Name == name) ?? statics.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NoSingleton(Type type) => $"{type.Name} has no singleton; try {type.Name}#0 or {type.Name} <static>";

    private static Type MemberType(MemberInfo m) => m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;

    // One member's value, or why it wasn't read: a denied getter, or the getter's exception.
    private static (object? Value, string? Failure) Read(MemberInfo m, object? obj)
    {
        if (s_denied.TryGetValue($"{m.DeclaringType?.FullName}.{m.Name}", out string? shared))
            return (null, $"denied ({m.DeclaringType!.Name}.{m.Name} creates a copy; use {shared})");
        if (LazyField(m, obj) is { } lazy) return (null, $"not read (lazy getter; {lazy} is null)");
        try
        {
            return (m is PropertyInfo p ? p.GetValue(obj) : ((FieldInfo)m).GetValue(obj), null);
        }
        catch (Exception e)
        {
            return (null, $"? ({(e is TargetInvocationException { InnerException: { } inner } ? inner : e).GetType().Name})");
        }
    }

    // A computed property over a null field of its own name (DirLightProps over dirLightProps) is most likely a lazy
    // getter that creates the object on first read (RendererManager's property blocks do), so it isn't called; the
    // field's name is returned. Interop marks a real Il2Cpp field with a NativeFieldInfoPtr_<name> static.
    private static string? LazyField(MemberInfo m, object? obj)
    {
        if (m is not PropertyInfo p || m.DeclaringType is not { } type || IsField(type, p.Name)) return null;
        foreach (string name in new[] { p.Name, "_" + p.Name, "m_" + p.Name })
        {
            var field = type.GetProperties(obj == null ? BindingFlags.Public | BindingFlags.Static : InstanceMembers)
                .FirstOrDefault(f => f != p && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && IsField(type, f.Name) && !f.PropertyType.IsValueType);
            if (field == null) continue;
            try
            {
                return field.GetValue(obj) == null ? field.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    private static bool IsField(Type type, string name) =>
        type.GetField("NativeFieldInfoPtr_" + name, BindingFlags.NonPublic | BindingFlags.Static) != null;

    // ---- Collections ----

    // The item count of an interop array or list, a dictionary or set, or a managed collection; null for anything else.
    private static int? CountOf(object? value)
    {
        if (value is null or string or UnityEngine.Object) return null;
        if (value is System.Collections.ICollection c) return c.Count;
        var type = value.GetType();
        var count = type.GetProperty("Count", InstanceMembers, null, typeof(int), Type.EmptyTypes, null)
            ?? type.GetProperty("Length", InstanceMembers, null, typeof(int), Type.EmptyTypes, null);
        if (count == null || (Indexer(type) == null && type.GetMethod("GetEnumerator", Type.EmptyTypes) == null)) return null;
        try
        {
            return (int)count.GetValue(value)!;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // An int indexer (dictionaries are enumerated instead, even with int keys).
    private static PropertyInfo? Indexer(Type type) => IsDictionary(type) ? null
        : type.GetProperties(InstanceMembers).FirstOrDefault(p => p.GetIndexParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(int));

    private static PropertyInfo? KeyIndexer(Type type) =>
        type.GetProperties(InstanceMembers).FirstOrDefault(p => p.GetIndexParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(int));

    private static bool IsDictionary(Type type) => type.Name.StartsWith("Dictionary") || type.GetProperty("Keys", InstanceMembers) != null;

    // Up to max items: by index where there is an int indexer, otherwise by enumerating (dictionaries as key and value).
    private static IEnumerable<(string Label, object? Value)> ItemsOf(object collection, int max)
    {
        var type = collection.GetType();
        if (Indexer(type) is { } indexer && CountOf(collection) is int count)
        {
            for (int i = 0; i < Math.Min(count, max); i++) yield return ($"[{i}]", indexer.GetValue(collection, new object[] { i }));
            yield break;
        }

        int n = 0;
        if (collection is System.Collections.IEnumerable managed)
        {
            foreach (object? item in managed)
            {
                if (n >= max) yield break;
                yield return Entry(item, n++);
            }
            yield break;
        }
        // An Il2Cpp collection: its own enumerator, read through MoveNext and Current.
        object? enumerator = type.GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(collection, null);
        if (enumerator == null) yield break;
        var move = enumerator.GetType().GetMethod("MoveNext", Type.EmptyTypes);
        var current = enumerator.GetType().GetProperty("Current", InstanceMembers);
        if (move == null || current == null) yield break;
        while (n < max && (bool)move.Invoke(enumerator, null)!) yield return Entry(current.GetValue(enumerator), n++);
    }

    private static (string Label, object? Value) Entry(object? item, int n)
    {
        if (item == null) return ($"[{n}]", null);
        var type = item.GetType();
        if (type.GetProperty("Key", InstanceMembers) is { } key && type.GetProperty("Value", InstanceMembers) is { } value)
            return ($"[{Format(key.GetValue(item))}]", value.GetValue(item));
        return ($"[{n}]", item);
    }

    // ---- Formatting ----

    private static string ValueLine(Node node) =>
        $"{node.Name}: {TypeName(node.Value?.GetType() ?? node.DeclaredType)} = {Format(node.Value)}";

    // The header of an object listing: its type, and its name for a Unity object.
    private static string Describe(object value) =>
        value is UnityEngine.Object u ? $"{TypeName(value.GetType())} '{u.name}'" : TypeName(value.GetType());

    // Values that print as one line rather than a member listing.
    private static bool IsPlain(object? value) =>
        value is null or string || value.GetType().IsPrimitive || value.GetType().IsEnum || value.GetType().IsValueType;

    /// <summary>A value as the inspection commands print it.</summary>
    public static string Format(object? value)
    {
        static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        switch (value)
        {
            case null: return "null";
            case string s: return "\"" + (s.Length > MaxString ? s[..MaxString] + "…" : s).Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
            case bool b: return b.ToString();
            case float f: return F(f);
            case double d: return F(d);
            case Enum e: return $"{e} ({Convert.ToString(Convert.ChangeType(e, Enum.GetUnderlyingType(e.GetType()), CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)})";
            case Vector2 v: return $"({F(v.x)}, {F(v.y)})";
            case Vector3 v: return $"({F(v.x)}, {F(v.y)}, {F(v.z)})";
            case Vector4 v: return $"({F(v.x)}, {F(v.y)}, {F(v.z)}, {F(v.w)})";
            case Quaternion q: return $"({F(q.x)}, {F(q.y)}, {F(q.z)}, {F(q.w)})";
            case Color c: return $"({F(c.r)}, {F(c.g)}, {F(c.b)}, {F(c.a)})";
            case UnityEngine.Object u: return Game.Alive(u) ? $"{u.GetIl2CppType().Name} '{u.name}'" : "destroyed";
        }
        var type = value.GetType();
        if (type.IsPrimitive) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (CountOf(value) is int count) return $"[{count}]";
        if (type.IsValueType) return StructText(value);
        return value is Il2CppObjectBase obj && !Game.Alive(obj) ? "collected" : TypeName(type);
    }

    // A struct's own text, or, where that is only its type name (LayerMask, Scene), its simple members.
    private static string StructText(object value)
    {
        // One line per value: Matrix4x4 prints its rows on lines of their own.
        string text = Regex.Replace(value.ToString() ?? string.Empty, @"\s+", " ").Trim();
        if (text != value.GetType().FullName) return text;
        var parts = value.GetType().GetProperties(InstanceMembers)
            .Where(p => p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)))
            .Take(6).Select(p => $"{p.Name} {Format(Read(p, value).Value)}");
        return "{" + string.Join(", ", parts) + "}";
    }

    /// <summary>A type's name without namespaces, generics written out: List&lt;Buff&gt;.</summary>
    public static string TypeName(Type type)
    {
        if (!type.IsGenericType) return type.Name;
        string name = type.Name;
        int tick = name.IndexOf('`');
        return (tick >= 0 ? name[..tick] : name) + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
    }
}
