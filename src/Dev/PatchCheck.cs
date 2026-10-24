using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using EpochAssist.Core;
using Iced.Intel;
using Il2CppInterop.Runtime;

namespace EpochAssist.Dev;

/// <summary>
/// Dev only: checks every patch target of every registered feature before a release. Each target's native code is found
/// through the il2cpp runtime and its first bytes are read from GameAssembly.dll on disk, where our own detours can't
/// hide them, then decoded. Reads only: nothing is patched or changed. The result goes to dev/patch-check.json, which
/// tools/release.sh requires.
/// </summary>
internal static class PatchCheck
{
    // MelonLoader's detour overwrites a method's first 6 bytes; a short branch starting in them is relocated wrongly and
    // the game crashes (int3). Found with the reference mod's HookSafety test and confirmed in game.
    private const int DetourBytes = 6;
    private const int ReadBytes = 32;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private sealed record Row(string Feature, string? Group, string Patch, string Target, string Outcome, string Detail);

    public static CommandResult Run(string gameVersion)
    {
        int before = HarmonyLib.Harmony.GetAllPatchedMethods().Count();
        var module = GameModule();
        var rows = new List<Row>();
        foreach (var feature in FeatureHost.All)
        {
            foreach (var target in Compat.PatchTargets(feature))
            {
                (string outcome, string detail) = Check(target, module);
                rows.Add(new Row(feature.Id, target.Group, target.Patch.Name, target.Name, outcome, detail));
            }
        }
        int after = HarmonyLib.Harmony.GetAllPatchedMethods().Count();
        bool pass = rows.All(r => r.Outcome == "safe");

        string file = Path.Combine(DevBridge.Dir, "patch-check.json");
        try
        {
            var result = new
            {
                format = 1,
                game_version = gameVersion,
                mod_version = BuildInfo.Version,
                build_id = Runtime.BuildId,
                checked_at = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                targets = rows.Select(r => new { feature = r.Feature, group = r.Group, patch = r.Patch, target = r.Target, outcome = r.Outcome, detail = r.Detail }),
                result = pass ? "pass" : "fail",
            };
            Directory.CreateDirectory(DevBridge.Dir);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(result, s_json));
            File.Move(tmp, file, true);
        }
        catch (Exception e)
        {
            return CommandResult.Error($"patchcheck {gameVersion}: could not save dev/patch-check.json ({e.GetType().Name}: {e.Message})");
        }

        string counts = string.Join(", ", rows.GroupBy(r => r.Outcome).OrderBy(g => g.Key == "safe" ? 0 : 1)
            .Select(g => $"{g.Count()} {g.Key.Replace('_', ' ')}"));
        var lines = new List<string>
        {
            $"ok patchcheck {gameVersion}: {(pass ? "pass" : "fail")}, {rows.Count} targets ({counts}), patched methods {(before == after ? $"{before} before and after" : $"{before} before, {after} after")}, saved dev/patch-check.json",
        };
        lines.AddRange(rows.Where(r => r.Outcome != "safe").Select(r => $"  {r.Feature} {r.Target} (patch {r.Patch}): {r.Detail}"));
        return new CommandResult(string.Join("\n", lines));
    }

    // One outcome per target, from the first rule that applies: missing, ambiguous, unreadable, short_branch, safe.
    private static (string Outcome, string Detail) Check(PatchTarget target, ProcessModule? module)
    {
        if (target.Error != null) return ("missing", $"the patch attribute couldn't be read ({target.Error})");
        if (target.Matches.Count == 0) return ("missing", "no method matches");
        if (target.Matches.Count > 1) return ("ambiguous", $"ambiguous: {target.Matches.Count} methods");
        if (module == null) return ("unreadable", "GameAssembly.dll isn't loaded");

        var natives = NativeMethods(target.Matches[0]);
        if (natives.Count != 1) return ("unreadable", $"{natives.Count} native methods match");
        IntPtr code = Marshal.ReadIntPtr(natives[0]);
        long rva = (long)code - (long)module.BaseAddress;
        if (rva < 0 || rva >= module.ModuleMemorySize) return ("unreadable", $"code at 0x{code:X} is outside GameAssembly.dll");
        if (ReadDisk(module.FileName!, rva, out byte[] bytes) is { } error) return ("unreadable", error);

        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), 0, DecoderOptions.None);
        while (decoder.IP < DetourBytes)
        {
            ulong at = decoder.IP;
            decoder.Decode(out var instruction);
            if (instruction.IsInvalid) return ("unreadable", $"invalid instruction at +{at}");
            if (instruction.IsJccShort || instruction.IsJmpShort || instruction.IsLoop || instruction.IsLoopcc || instruction.IsJcxShort)
                return ("short_branch", $"short branch at +{at} ({instruction})");
        }
        return ("safe", string.Empty);
    }

    // The runtime's own MethodInfo pointers that fit a managed interop method: its class's methods with the same name and
    // parameter count. The runtime's MethodInfo isn't touched by a detour; its first field is the original code address.
    private static List<IntPtr> NativeMethods(MethodBase method)
    {
        var found = new List<IntPtr>();
        IntPtr klass = Il2CppClassPointerStore.GetNativeClassPointer(method.DeclaringType!);
        if (klass == IntPtr.Zero) return found;
        int count = method.GetParameters().Length;
        IntPtr iter = IntPtr.Zero;
        IntPtr info;
        while ((info = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(info)) == method.Name && IL2CPP.il2cpp_method_get_param_count(info) == count) found.Add(info);
        }
        return found;
    }

    /// <summary>rva &lt;type&gt; &lt;method&gt;: every overload's code address in GameAssembly.dll, for a native decompile.</summary>
    public static string Rva(string typeName, string method)
    {
        var lookup = GameTypes.Resolve(typeName);
        if (lookup.Type is not { } type) return $"error rva: {lookup.Error}";
        if (GameModule() is not { } module) return "error rva: GameAssembly.dll isn't loaded";
        IntPtr klass = Il2CppClassPointerStore.GetNativeClassPointer(type);
        if (klass == IntPtr.Zero) return $"error rva: {type.FullName} has no il2cpp class";
        var lines = new List<string>();
        IntPtr iter = IntPtr.Zero;
        IntPtr info;
        while ((info = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
        {
            if (Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(info)) != method) continue;
            uint count = IL2CPP.il2cpp_method_get_param_count(info);
            var parameters = Enumerable.Range(0, (int)count)
                .Select(i => Marshal.PtrToStringAnsi(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_param(info, (uint)i))) + " "
                    + Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_param_name(info, (uint)i)));
            long rva = (long)Marshal.ReadIntPtr(info) - (long)module.BaseAddress;
            lines.Add($"  {method}({string.Join(", ", parameters)}): rva 0x{rva:X}");
        }
        return lines.Count == 0 ? $"error rva: no method {type.FullName}.{method}"
            : $"ok rva {type.FullName}.{method}: {lines.Count} overloads\n" + string.Join("\n", lines);
    }

    private static ProcessModule? GameModule() =>
        Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));

    // The bytes at an RVA as the file has them: the PE section table maps the RVA to a file offset. Null, or why not.
    private static string? ReadDisk(string path, long rva, out byte[] bytes)
    {
        bytes = new byte[ReadBytes];
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(file);
            file.Position = 0x3C;
            int pe = reader.ReadInt32();
            file.Position = pe + 4 + 2;
            int sections = reader.ReadUInt16();
            file.Position = pe + 4 + 16;
            int optional = reader.ReadUInt16();
            long table = pe + 4 + 20 + optional;
            for (int i = 0; i < sections; i++)
            {
                file.Position = table + i * 40 + 8;
                uint virtualSize = reader.ReadUInt32();
                uint virtualAddress = reader.ReadUInt32();
                uint rawSize = reader.ReadUInt32();
                uint rawPointer = reader.ReadUInt32();
                if (rva < virtualAddress || rva + ReadBytes > virtualAddress + Math.Min(virtualSize, rawSize)) continue;
                file.Position = rawPointer + (rva - virtualAddress);
                return reader.Read(bytes, 0, ReadBytes) == ReadBytes ? null : "GameAssembly.dll ends early";
            }
            return $"rva 0x{rva:X} is in no section of GameAssembly.dll";
        }
        catch (Exception e)
        {
            return $"GameAssembly.dll is unreadable ({e.GetType().Name})";
        }
    }
}
