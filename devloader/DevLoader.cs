using System.Reflection;
using System.Runtime.Loader;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(EpochAssist.DevLoader.DevLoader), "Epoch Assist Dev Loader", "1.0.0", "Crash")]
[assembly: MelonGame("Eleventh Hour Games", "Last Epoch")]

namespace EpochAssist.DevLoader;

/// <summary>
/// Loads EpochAssist.dll from UserData/EpochAssist/dev as bytes (so the file is never locked) and drives its
/// Runtime class. When the file changes, or on Ctrl+F5 (not while typing), the old copy is shut down and a fresh one is loaded.
/// Old load contexts are left loaded: Harmony does not cope well with unloading, and a few MB per reload is fine.
/// </summary>
public class DevLoader : MelonMod
{
    private const string RuntimeType = "EpochAssist.Core.Runtime";
    private const float PollSeconds = 0.5f;
    // A file must stop changing for this long before it is loaded, so a copy in progress is never read.
    private const double SettleSeconds = 0.4;

    private string _dllPath = string.Empty;
    private DateTime _loadedStamp;
    private DateTime _pendingStamp;
    private float _nextPollAt;
    private int _generation;
    private bool _missingLogged;

    private Action? _update, _gui, _scene, _save, _shutdown;
    private Func<bool>? _typing;

    public override void OnInitializeMelon()
    {
        string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "EpochAssist", "dev");
        Directory.CreateDirectory(dir);
        _dllPath = Path.Combine(dir, "EpochAssist.dll");
        TryLoad("start");
    }

    public override void OnUpdate()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (ctrl && Input.GetKeyDown(KeyCode.F5) && !Typing())
        {
            TryLoad("hotkey");
        }
        else if (Time.unscaledTime >= _nextPollAt)
        {
            _nextPollAt = Time.unscaledTime + PollSeconds;
            PollForChange();
        }

        Call(_update, "update");
    }

    public override void OnGUI() => Call(_gui, "gui");
    public override void OnSceneWasLoaded(int buildIndex, string sceneName) => Call(_scene, "scene");
    public override void OnSceneWasUnloaded(int buildIndex, string sceneName) => Call(_scene, "scene");
    public override void OnApplicationQuit() => Call(_save, "save");
    public override void OnDeinitializeMelon() => Unload();

    // Builds without Runtime.Typing, or one that throws, don't block the reload key.
    private bool Typing()
    {
        try
        {
            return _typing?.Invoke() ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void PollForChange()
    {
        if (!File.Exists(_dllPath)) return;
        DateTime stamp = File.GetLastWriteTimeUtc(_dllPath);
        if (stamp == _loadedStamp) return;

        if (stamp != _pendingStamp)
        {
            _pendingStamp = stamp;
            return;
        }
        if ((DateTime.UtcNow - stamp).TotalSeconds >= SettleSeconds) TryLoad("file changed");
    }

    private void TryLoad(string reason)
    {
        if (!File.Exists(_dllPath))
        {
            if (!_missingLogged) LoggerInstance.Warning($"Waiting for {_dllPath} (run ./build.sh dev)");
            _missingLogged = true;
            return;
        }
        _missingLogged = false;

        DateTime stamp = File.GetLastWriteTimeUtc(_dllPath);
        byte[] dll, pdb;
        try
        {
            dll = File.ReadAllBytes(_dllPath);
            string pdbPath = Path.ChangeExtension(_dllPath, ".pdb");
            pdb = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : Array.Empty<byte>();
        }
        catch (IOException e)
        {
            LoggerInstance.Warning($"Could not read the build yet: {e.Message}");
            return;
        }

        Unload();
        _loadedStamp = stamp;
        _pendingStamp = stamp;
        _generation++;

        try
        {
            var context = new AssemblyLoadContext($"EpochAssist#{_generation}");
            using var dllStream = new MemoryStream(dll);
            using var pdbStream = pdb.Length > 0 ? new MemoryStream(pdb) : null;
            Assembly assembly = context.LoadFromStream(dllStream, pdbStream);

            Type runtime = assembly.GetType(RuntimeType, throwOnError: true)!;
            _update = Bind(runtime, "Update");
            _gui = Bind(runtime, "OnGUI");
            _scene = Bind(runtime, "SceneChanged");
            _save = Bind(runtime, "Save");
            _shutdown = Bind(runtime, "Shutdown");
            var typing = runtime.GetProperty("Typing", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            _typing = typing != null ? (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), typing) : null;

            runtime.GetMethod("Init", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object[] { true });
            LoggerInstance.Msg($"Loaded build #{_generation} ({reason})");
        }
        catch (Exception e)
        {
            LoggerInstance.Error($"Build #{_generation} failed to load: {(e as TargetInvocationException)?.InnerException ?? e}");
            // Init may have started and patched features before it threw; undo them before dropping the build.
            Call(_shutdown, "shutdown after failed load");
            Clear();
        }
    }

    private void Unload()
    {
        if (_shutdown == null) return;
        Call(_shutdown, "shutdown");
        Clear();
    }

    private void Clear()
    {
        _update = _gui = _scene = _save = _shutdown = null;
        _typing = null;
    }

    private static Action Bind(Type type, string method) =>
        (Action)Delegate.CreateDelegate(typeof(Action), type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, method));

    // The mod handles its own errors per feature; anything reaching here is a bug in the runtime itself.
    private void Call(Action? action, string what)
    {
        if (action == null) return;
        try
        {
            action();
        }
        catch (Exception e)
        {
            LoggerInstance.Error($"Runtime {what} failed: {e}");
        }
    }
}
