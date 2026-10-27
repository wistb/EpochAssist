using EpochAssist.Core;
using UnityEngine;

namespace EpochAssist.Dev;

/// <summary>
/// The bridge's input verbs, named after tools/devloader/input.ps1's steps: they press keys, click, hold, point and
/// type inside the game through VirtualInput, without moving the real cursor or taking focus. Coordinates are
/// game-window pixels from the top left. There is no wheel (Unity takes no posted wheel); PageUp and PageDown scroll
/// the menu. Offline only, and bridge only, so the end of a batch can let go of what a verb left held.
/// </summary>
internal static class InputCommands
{

    private const int DefaultHoldMs = 60;

    // input.ps1 takes Windows Forms key names; these are the ones whose Unity name differs.
    private static readonly Dictionary<string, KeyCode> s_aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LShiftKey"] = KeyCode.LeftShift, ["RShiftKey"] = KeyCode.RightShift, ["ShiftKey"] = KeyCode.LeftShift, ["Shift"] = KeyCode.LeftShift,
        ["LControlKey"] = KeyCode.LeftControl, ["RControlKey"] = KeyCode.RightControl, ["ControlKey"] = KeyCode.LeftControl, ["Ctrl"] = KeyCode.LeftControl,
        ["Menu"] = KeyCode.LeftAlt, ["LMenu"] = KeyCode.LeftAlt, ["RMenu"] = KeyCode.RightAlt, ["Alt"] = KeyCode.LeftAlt,
        ["Enter"] = KeyCode.Return, ["Back"] = KeyCode.Backspace, ["Next"] = KeyCode.PageDown, ["Prior"] = KeyCode.PageUp,
        ["Up"] = KeyCode.UpArrow, ["Down"] = KeyCode.DownArrow, ["Left"] = KeyCode.LeftArrow, ["Right"] = KeyCode.RightArrow,
        ["Capital"] = KeyCode.CapsLock, ["OemPeriod"] = KeyCode.Period, ["OemMinus"] = KeyCode.Minus, ["Oemcomma"] = KeyCode.Comma,
        ["Oemplus"] = KeyCode.Equals, ["OemQuestion"] = KeyCode.Slash, ["OemSemicolon"] = KeyCode.Semicolon, ["OemQuotes"] = KeyCode.Quote,
        ["OemOpenBrackets"] = KeyCode.LeftBracket, ["OemCloseBrackets"] = KeyCode.RightBracket, ["OemPipe"] = KeyCode.Backslash,
        ["Oemtilde"] = KeyCode.BackQuote, ["LWin"] = KeyCode.LeftWindows, ["RWin"] = KeyCode.RightWindows,
    };

    // Typed characters: the key, and whether Shift goes with it (US layout, as input.ps1 assumes).
    private static readonly Dictionary<char, (KeyCode Key, bool Shift)> s_chars = BuildChars();

    public static void Register(string owner)
    {
        void Add(string name, string usage, string help, Func<CommandCall, CommandResult> run) =>
            Commands.Register(new Command(name, usage, help, CommandMark.Dev, owner, call => Guard(call) ?? run(call)));

        Add("key", "key <Key> [ms]", "Presses and releases a key inside the game (60 ms when not given).", Key);
        Add("down", "down <Key>", "Holds a key inside the game until up or the end of the batch.", call => Hold(call, true));
        Add("up", "up <Key>", "Releases a key the bridge holds.", call => Hold(call, false));
        Add("click", "click <x> <y> [left|right|middle]", "Clicks inside the game at a game-window pixel (from the top left).", Click);
        Add("press", "press <x> <y> <left|right|middle> <ms>", "Holds a mouse button at a game-window pixel for a time.", Press);
        Add("cursor", "cursor <x> <y>", "Moves the injected pointer to a game-window pixel; a held button drags.", Cursor);
        Add("button", "button down|up <left|right|middle> [x y]", "Holds or releases a mouse button inside the game, for drags.", Button);
        Add("type", "type <text>", "Types text inside the game: letters, digits, space and US-layout punctuation.", Type);
        Add("input", "input", "Shows the bridge's injected keys, buttons and pointer, and real and injected focus.", _ =>
            CommandResult.Ok($"input: {VirtualInput.Describe()}, focused real {InputState.RealFocused}"));
    }

    private static CommandResult? Guard(CommandCall call)
    {
        if (call.Name == "input") return null;
        if (GameState.Online == true) return CommandResult.Error($"{call.Name}: offline only");
        if (call.Source != "bridge") return CommandResult.Error($"{call.Name}: bridge only (a batch's end lets go of what it holds)");
        return null;
    }

    // ---- Verbs ----

    private static CommandResult Key(CommandCall call)
    {
        if (!TryKey(call, 1, out KeyCode key, out var error)) return error!;
        int ms = DefaultHoldMs;
        if (call.Args.Length > 1 && !int.TryParse(call.Args[1], out ms)) return CommandResult.Error("usage: key <Key> [ms]");
        int pressedAt = 0;
        return Run($"key {key}", new Script()
            .Do(() => { VirtualInput.QueueKey(key, true); pressedAt = Time.frameCount; })
            .Wait(ms)
            .Do(() => VirtualInput.QueueKey(key, false)),
            () => $"key {key} ({Time.frameCount - pressedAt} frames)");
    }

    private static CommandResult Hold(CommandCall call, bool down)
    {
        if (!TryKey(call, 1, out KeyCode key, out var error)) return error!;
        if (!down && !VirtualInput.IsHeld(key)) return CommandResult.Ok($"up {key} (was not held)");
        VirtualInput.QueueKey(key, down);
        return CommandResult.Ok($"{(down ? "down" : "up")} {key}") with { Settle = 1 };
    }

    private static CommandResult Click(CommandCall call)
    {
        if (!TryPoint(call, 1, out var at, out var error) || (error = OffModUi(call, at)) != null) return error!;
        int button = 0;
        if (call.Args.Length > 2 && !TryButton(call.Lower(3), out button)) return CommandResult.Error("usage: click <x> <y> [left|right|middle]");
        return Run($"click {at.x:F0} {at.y:F0} {VirtualInput.ButtonName(button)}", new Script()
            .Do(() => VirtualInput.QueuePointer(at))
            .Do(() => VirtualInput.PostButton(button, true))
            .Wait(DefaultHoldMs)
            .Do(() => VirtualInput.PostButton(button, false)));
    }

    private static CommandResult Press(CommandCall call)
    {
        if (!TryPoint(call, 1, out var at, out var error) || (error = OffModUi(call, at)) != null) return error!;
        if (call.Args.Length < 4 || !TryButton(call.Lower(3), out int button) || !int.TryParse(call.Args[3], out int ms))
            return CommandResult.Error("usage: press <x> <y> <left|right|middle> <ms>");
        return Run($"press {at.x:F0} {at.y:F0} {VirtualInput.ButtonName(button)} {ms}", new Script()
            .Do(() => VirtualInput.QueuePointer(at))
            .Do(() => VirtualInput.PostButton(button, true))
            .Wait(ms)
            .Do(() => VirtualInput.PostButton(button, false)));
    }

    // Moving is allowed anywhere: it only moves the pointer the mod and IMGUI see, and drags a held button.
    private static CommandResult Cursor(CommandCall call)
    {
        if (!TryPoint(call, 1, out var at, out var error)) return error!;
        VirtualInput.QueuePointer(at);
        return CommandResult.Ok($"cursor {at.x:F0} {at.y:F0}") with { Settle = 1 };
    }

    private static CommandResult Button(CommandCall call)
    {
        string dir = call.Lower(1);
        if ((dir != "down" && dir != "up") || !TryButton(call.Lower(2), out int button))
            return CommandResult.Error("usage: button down|up <left|right|middle> [x y]");
        bool down = dir == "down";
        if (!down && !VirtualInput.IsHeld(button)) return CommandResult.Ok($"button up {VirtualInput.ButtonName(button)} (was not held)");
        Vector2? at = VirtualInput.Pointer;
        if (call.Args.Length > 2)
        {
            if (!TryPoint(call, 3, out var point, out var error)) return error!;
            at = point;
        }
        if (at is not { } p) return CommandResult.Error("button: give x y, or set the pointer with cursor first");
        if (down && OffModUi(call, p) is { } off) return off;
        string reply = $"button {dir} {VirtualInput.ButtonName(button)} {p.x:F0} {p.y:F0}";
        // The pointer moves first, so the press lands where it is aimed.
        return Run(reply, new Script().Do(() => VirtualInput.QueuePointer(p)).Do(() => VirtualInput.PostButton(button, down)));
    }

    // Unity takes a posted click as a real mouse button. The game's world ignores it, and its UI aims at the bridge
    // pointer while GameUiPointer's patch is in; without the patch the UI would take it at the real cursor, so presses
    // then go only where the mod's menu or HUD takes them.
    private static CommandResult? OffModUi(CommandCall call, Vector2 at) =>
        // The menu's hit test is its rectangle whether or not it is open.
        GameUiPointer.Available || (Ui.Menu.Visible && InputGate.MenuHitTest?.Invoke(at) == true) || InputGate.HudHitTest?.Invoke(at) == true
            ? null
            : CommandResult.Error($"{call.Name}: {at.x:F0} {at.y:F0} is not on the menu or the HUD, and the game UI pointer patch is missing (the game would take the click at the real cursor)");

    private static CommandResult Type(CommandCall call)
    {
        // The text keeps its case; the bridge has already trimmed the line.
        string text = call.Line.Length > 5 ? call.Line.Substring(5) : string.Empty;
        if (text.Length == 0) return CommandResult.Error("usage: type <text>");
        foreach (char c in text)
        {
            if (!s_chars.ContainsKey(c)) return CommandResult.Error($"type: no key types '{c}' (letters, digits, space and US-layout punctuation)");
        }
        var script = new Script();
        foreach (char c in text)
        {
            var (key, shift) = s_chars[c];
            if (shift) script.Do(() => VirtualInput.QueueKey(KeyCode.LeftShift, true));
            script.Do(() => VirtualInput.QueueKey(key, true, c)).Do(() => VirtualInput.QueueKey(key, false));
            if (shift) script.Do(() => VirtualInput.QueueKey(KeyCode.LeftShift, false));
        }
        return Run($"type \"{text}\"", script);
    }

    // ---- Steps over frames ----

    // One action a frame (each takes effect at the next frame's first read), with timed waits between them; the bridge
    // asks once a frame and moves on when the script is through.
    private sealed class Script
    {
        private readonly List<(Action? Act, float Seconds)> _steps = new();
        private int _next;
        private float _until;

        public Script Do(Action act)
        {
            _steps.Add((act, 0f));
            return this;
        }

        public Script Wait(int ms)
        {
            _steps.Add((null, ms / 1000f));
            return this;
        }

        /// <summary>Runs the next step if its time has come; true once every step has run and one more frame passed.</summary>
        public bool Step()
        {
            if (Time.unscaledTime < _until) return false;
            if (_next == _steps.Count) return true;
            var (act, seconds) = _steps[_next++];
            act?.Invoke();
            _until = Time.unscaledTime + seconds;
            return false;
        }
    }

    private static CommandResult Run(string reply, Script script, Func<string>? finalReply = null)
    {
        script.Step();
        return CommandResult.Ok(reply) with { Text = string.Empty, Pending = () => script.Step() ? "ok " + (finalReply?.Invoke() ?? reply) : null };
    }

    // ---- Arguments ----

    private static bool TryKey(CommandCall call, int index, out KeyCode key, out CommandResult? error)
    {
        string name = index <= call.Args.Length ? call.Args[index - 1] : string.Empty;
        error = null;
        if (ParseKey(name) is { } parsed)
        {
            key = parsed;
            return true;
        }
        key = KeyCode.None;
        error = name.Length == 0
            ? CommandResult.Error($"usage: {call.Name} <Key>")
            : CommandResult.Error($"{call.Name}: unknown key '{name}' (Unity KeyCode names such as F2, LeftShift, Alpha1, or input.ps1 names such as LShiftKey, D1, OemPeriod; mouse buttons use click, press and button)");
        return false;
    }

    private static KeyCode? ParseKey(string name)
    {
        if (name.Length == 0) return null;
        if (s_aliases.TryGetValue(name, out var alias)) return alias;
        // input.ps1's digits (D0 to D9) and a bare digit.
        if (name.Length == 2 && (name[0] == 'D' || name[0] == 'd') && char.IsDigit(name[1])) return KeyCode.Alpha0 + (name[1] - '0');
        if (name.Length == 1 && char.IsDigit(name[0])) return KeyCode.Alpha0 + (name[0] - '0');
        if (name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && name.Length == 7 && char.IsDigit(name[6])) return KeyCode.Keypad0 + (name[6] - '0');
        // Names only: Enum.TryParse would also take a number.
        if (char.IsDigit(name[0]) || name[0] == '-') return null;
        if (!Enum.TryParse(name, ignoreCase: true, out KeyCode key) || !Enum.IsDefined(typeof(KeyCode), key)) return null;
        if (key == KeyCode.None || key >= KeyCode.Mouse0) return null;
        return key;
    }

    private static bool TryButton(string name, out int button)
    {
        button = name switch { "left" => 0, "right" => 1, "middle" => 2, _ => -1 };
        return button >= 0;
    }

    private static bool TryPoint(CommandCall call, int index, out Vector2 at, out CommandResult? error)
    {
        at = default;
        error = null;
        if (call.Args.Length < index + 1 || !int.TryParse(call.Args[index - 1], out int x) || !int.TryParse(call.Args[index], out int y))
        {
            error = CommandResult.Error($"{call.Name}: x and y are game-window pixels from the top left");
            return false;
        }
        if (x < 0 || y < 0 || x >= Screen.width || y >= Screen.height)
        {
            error = CommandResult.Error($"{call.Name}: {x} {y} is outside the game window ({Screen.width}x{Screen.height})");
            return false;
        }
        at = new Vector2(x, y);
        return true;
    }

    private static Dictionary<char, (KeyCode, bool)> BuildChars()
    {
        var map = new Dictionary<char, (KeyCode, bool)>();
        for (char c = 'a'; c <= 'z'; c++)
        {
            map[c] = (KeyCode.A + (c - 'a'), false);
            map[char.ToUpperInvariant(c)] = (KeyCode.A + (c - 'a'), true);
        }
        const string shiftedDigits = ")!@#$%^&*(";
        for (int d = 0; d <= 9; d++)
        {
            map[(char)('0' + d)] = (KeyCode.Alpha0 + d, false);
            map[shiftedDigits[d]] = (KeyCode.Alpha0 + d, true);
        }
        void Pair(KeyCode key, char plain, char shifted)
        {
            map[plain] = (key, false);
            map[shifted] = (key, true);
        }
        map[' '] = (KeyCode.Space, false);
        Pair(KeyCode.Period, '.', '>');
        Pair(KeyCode.Comma, ',', '<');
        Pair(KeyCode.Minus, '-', '_');
        Pair(KeyCode.Equals, '=', '+');
        Pair(KeyCode.Slash, '/', '?');
        Pair(KeyCode.Semicolon, ';', ':');
        Pair(KeyCode.Quote, '\'', '"');
        Pair(KeyCode.LeftBracket, '[', '{');
        Pair(KeyCode.RightBracket, ']', '}');
        Pair(KeyCode.Backslash, '\\', '|');
        Pair(KeyCode.BackQuote, '`', '~');
        return map;
    }
}
