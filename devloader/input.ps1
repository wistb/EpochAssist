# Sends real keyboard and mouse input to the game window, for in-game checks driven from WSL (offline only).
# Dev only; never shipped. Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File devloader/input.ps1 <step> [<step>...]
# Steps:  focus | restore | wait <ms> | key <Key> [ms] | down <Key> | up <Key> | type <text> (letters, digits, space, . and -)
#         click <x> <y> [left|right|middle|x1|x2] | press <x> <y> <button> <ms> | move <x> <y> | wheel <x> <y> <notches, + up> | shot <png path>
#         (x, y: pixels in the game window's client area, top-left origin; Key: a System.Windows.Forms.Keys name such as
#         F2, Q, Return, Escape, LControlKey, RControlKey, LShiftKey, Menu)
# "focus" brings the game to the front; "restore" gives focus back to the window that had it before.
# The PC may be in use, so: "focus" first waits until there has been no keyboard or mouse input for IdleSeconds (up to
# MaxWaitSeconds, else it exits 3 without touching anything); during a run a low-level hook watches for input that isn't
# ours, and any key, click or mouse move by a person stops the run at once (exit 2); and nothing is sent unless the game
# is the foreground window (exit 1). A stop lets go of modifiers and gives focus back only if the game still has it.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Steps)
$IdleSeconds = 4
$MaxWaitSeconds = 300
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public UNION u; }
    [StructLayout(LayoutKind.Explicit)] public struct UNION {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }

    public static void Key(ushort vk, bool up, bool extended) {
        var i = new INPUT[1];
        i[0].type = 1;
        i[0].u.ki.scan = (ushort)MapVirtualKey(vk, 0);
        i[0].u.ki.flags = 0x0008 | (up ? 0x0002u : 0u) | (extended ? 0x0001u : 0u);
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint size, time; }

    /// <summary>Milliseconds since the last keyboard or mouse input from anyone.</summary>
    public static uint IdleMs() {
        var info = new LASTINPUTINFO { size = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        GetLastInputInfo(ref info);
        return (uint)Environment.TickCount - info.time;
    }

    /// <summary>A mouse move to screen pixels through SendInput, so the watch hook sees it as injected.</summary>
    public static void MoveTo(int x, int y, int left, int top, int width, int height) {
        var i = new INPUT[1];
        i[0].type = 0;
        i[0].u.mi.dx = (int)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1));
        i[0].u.mi.dy = (int)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1));
        i[0].u.mi.flags = 0x0001 | 0x8000 | 0x4000;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }

    public static void Mouse(uint flags, uint data) {
        var i = new INPUT[1];
        i[0].type = 0;
        i[0].u.mi.flags = flags;
        i[0].u.mi.data = data;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
// Low-level hooks on their own thread: set Active when a key, button, wheel or move arrives that wasn't injected.
public static class Watch {
    private delegate IntPtr Proc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, Proc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    private static readonly Proc s_key = OnKey, s_mouse = OnMouse;
    private static IntPtr s_keyHook, s_mouseHook;
    public static volatile bool Active;
    // Where our last injected move went: Windows can echo it as a plain move there, which isn't a person.
    public static volatile int ExpectX = int.MinValue, ExpectY = int.MinValue;
    public static volatile string What = "";

    public static void Start() {
        var ready = new ManualResetEvent(false);
        var thread = new Thread(() => {
            IntPtr module = GetModuleHandle(null);
            s_keyHook = SetWindowsHookEx(13, s_key, module, 0);
            s_mouseHook = SetWindowsHookEx(14, s_mouse, module, 0);
            ready.Set();
            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
        });
        thread.IsBackground = true;
        thread.Start();
        ready.WaitOne(2000);
    }

    public static bool Hooked { get { return s_keyHook != IntPtr.Zero && s_mouseHook != IntPtr.Zero; } }

    public static void Reset() { Active = false; What = ""; }

    // KBDLLHOOKSTRUCT.flags at 8, LLKHF_INJECTED 0x10.
    private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam) {
        if (code >= 0 && (Marshal.ReadInt32(lParam, 8) & 0x10) == 0) { Active = true; What = "a key"; }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // MSLLHOOKSTRUCT.flags at 12, LLMHF_INJECTED 0x01.
    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam) {
        bool echo = wParam.ToInt64() == 0x200 && Math.Abs(Marshal.ReadInt32(lParam, 0) - ExpectX) <= 8 && Math.Abs(Marshal.ReadInt32(lParam, 4) - ExpectY) <= 8;
        if (code >= 0 && !echo && (Marshal.ReadInt32(lParam, 12) & 0x01) == 0) { Active = true; What = "the mouse (message 0x" + wParam.ToInt64().ToString("x") + " at " + Marshal.ReadInt32(lParam, 0) + "," + Marshal.ReadInt32(lParam, 4) + ", flags " + Marshal.ReadInt32(lParam, 12) + ")"; }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }
}
"@

$game = Get-Process 'Last Epoch' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $game) { Write-Output 'error no game window'; exit 1 }
$hwnd = $game.MainWindowHandle
$previous = [W]::GetForegroundWindow()
$prevFile = Join-Path $env:TEMP 'epochassist-input-previous.txt'
[Watch]::Start()
# Without the watch a person's input can't be told apart, so nothing is sent.
if (-not [Watch]::Hooked) { Write-Output 'error the input watch could not start; nothing was sent'; exit 1 }

# Keys whose scancode needs the extended flag.
$extended = 'RControlKey', 'RMenu', 'Insert', 'Delete', 'Home', 'End', 'PageUp', 'PageDown', 'Up', 'Down', 'Left', 'Right', 'Divide', 'NumLock'

function Vk([string]$name) { [uint16][System.Windows.Forms.Keys]$name }
# Input goes to whatever window is in front, so every key and click first checks that it is still the game. If not, it
# stops: modifiers are let go (a key-up is harmless anywhere) and nothing more is sent.
function Guard {
    if ([Watch]::Active) { Stop 2 "you used $([Watch]::What)" }
    if ([W]::GetForegroundWindow() -ne $hwnd) { Stop 1 'the game lost focus' }
}

function Stop([int]$code, [string]$why) {
    foreach ($m in 'LShiftKey', 'RShiftKey', 'LControlKey', 'RControlKey', 'LMenu', 'RMenu') { [W]::Key((Vk $m), $true, $extended -contains $m) }
    # Give focus back only while the game holds it; a person who moved on keeps what they chose.
    if ([W]::GetForegroundWindow() -eq $hwnd) {
        $back = if (Test-Path $prevFile) { [IntPtr][int64](Get-Content $prevFile) } else { $previous }
        if ($back -ne $hwnd -and $back -ne [IntPtr]::Zero) { [void](Focus $back) }
    }
    Write-Output "error stopped: $why; the rest was not sent"
    exit $code
}

# Waits in small steps, stopping as soon as a person uses the keyboard or mouse.
function Pause([int]$ms) {
    $until = [Environment]::TickCount + $ms
    while ([Environment]::TickCount -lt $until) {
        if ([Watch]::Active) { Guard }
        Start-Sleep -Milliseconds ([Math]::Min(50, [Math]::Max(1, $until - [Environment]::TickCount)))
    }
}

# Before taking focus: no input from anyone for IdleSeconds, or give up after MaxWaitSeconds.
function WaitForIdle {
    $deadline = [Environment]::TickCount + $MaxWaitSeconds * 1000
    $said = $false
    while ([W]::IdleMs() -lt $IdleSeconds * 1000) {
        if ([Environment]::TickCount -gt $deadline) { Write-Output "error the PC stayed in use for $MaxWaitSeconds s; nothing was sent"; exit 3 }
        if (-not $said) { Write-Output "waiting until the keyboard and mouse are idle for $IdleSeconds s"; $said = $true }
        Start-Sleep -Milliseconds 250
    }
    [Watch]::Reset()
}

function KeyEvent([string]$name, [bool]$up) {
    if (-not $up) { Guard }
    [W]::Key((Vk $name), $up, $extended -contains $name)
}

function Focus([IntPtr]$target) {
    if ([W]::IsIconic($target)) { [void][W]::ShowWindow($target, 9) }
    $me = [W]::GetCurrentThreadId()
    $fgThread = [W]::GetWindowThreadProcessId([W]::GetForegroundWindow(), [IntPtr]::Zero)
    $targetThread = [W]::GetWindowThreadProcessId($target, [IntPtr]::Zero)
    # Windows only lets the foreground's input queue hand focus over, so join both queues for the switch.
    [void][W]::AttachThreadInput($me, $fgThread, $true)
    [void][W]::AttachThreadInput($me, $targetThread, $true)
    [void][W]::BringWindowToTop($target)
    [void][W]::SetForegroundWindow($target)
    [void][W]::AttachThreadInput($me, $fgThread, $false)
    [void][W]::AttachThreadInput($me, $targetThread, $false)
    Start-Sleep -Milliseconds 150
    return ([W]::GetForegroundWindow() -eq $target)
}

function Point([int]$x, [int]$y) {
    Guard
    $p = New-Object W+POINT
    $p.X = $x; $p.Y = $y
    [void][W]::ClientToScreen($hwnd, [ref]$p)
    $v = [System.Windows.Forms.SystemInformation]::VirtualScreen
    [Watch]::ExpectX = $p.X; [Watch]::ExpectY = $p.Y
    [W]::MoveTo($p.X, $p.Y, $v.Left, $v.Top, $v.Width, $v.Height)
}

$buttons = @{ left = @(0x0002, 0x0004, 0); right = @(0x0008, 0x0010, 0); middle = @(0x0020, 0x0040, 0); x1 = @(0x0080, 0x0100, 1); x2 = @(0x0080, 0x0100, 2) }

$i = 0
while ($i -lt $Steps.Count) {
    $step = $Steps[$i] -split ' '
    switch ($step[0]) {
        'focus' {
            WaitForIdle
            $previous = [W]::GetForegroundWindow()
            # Remember who had focus, across calls, so a later "restore" can give it back.
            if ($previous -ne $hwnd) { Set-Content -Path $prevFile -Value ([int64]$previous) }
            Write-Output "focus $(Focus $hwnd)"
        }
        'restore' {
            $back = if (Test-Path $prevFile) { [IntPtr][int64](Get-Content $prevFile) } else { $previous }
            if ($back -ne $hwnd -and $back -ne [IntPtr]::Zero) { Write-Output "restore $(Focus $back)" }
        }
        'wait' { Pause ([int]$step[1]) }
        'down' { KeyEvent $step[1] $false }
        'up' { KeyEvent $step[1] $true }
        'key' {
            $hold = if ($step.Count -gt 2) { [int]$step[2] } else { 60 }
            KeyEvent $step[1] $false; Start-Sleep -Milliseconds $hold; KeyEvent $step[1] $true; Start-Sleep -Milliseconds 40
        }
        'type' {
            # Letters (capitals with Shift), digits, space, '.' and '-'.
            foreach ($c in ($Steps[$i].Substring(5)).ToCharArray()) {
                $name = if ($c -eq ' ') { 'Space' } elseif ($c -eq '.') { 'OemPeriod' } elseif ($c -eq '-') { 'OemMinus' } elseif ([char]::IsDigit($c)) { "D$c" } else { [string]([char]::ToUpper($c)) }
                $shift = [char]::IsUpper($c)
                if ($shift) { KeyEvent 'LShiftKey' $false; Start-Sleep -Milliseconds 20 }
                KeyEvent $name $false; Start-Sleep -Milliseconds 30; KeyEvent $name $true; Start-Sleep -Milliseconds 40
                if ($shift) { KeyEvent 'LShiftKey' $true; Start-Sleep -Milliseconds 20 }
            }
        }
        'move' { Point ([int]$step[1]) ([int]$step[2]) }
        'click' {
            $b = $buttons[$(if ($step.Count -gt 3) { $step[3] } else { 'left' })]
            Point ([int]$step[1]) ([int]$step[2]); Start-Sleep -Milliseconds 30
            [W]::Mouse($b[0], $b[2]); Start-Sleep -Milliseconds 60; [W]::Mouse($b[1], $b[2]); Start-Sleep -Milliseconds 40
        }
        'press' {
            $b = $buttons[$step[3]]
            Point ([int]$step[1]) ([int]$step[2]); Start-Sleep -Milliseconds 30
            [W]::Mouse($b[0], $b[2]); Start-Sleep -Milliseconds ([int]$step[4]); [W]::Mouse($b[1], $b[2])
        }
        'wheel' {
            Point ([int]$step[1]) ([int]$step[2]); Start-Sleep -Milliseconds 30
            [W]::Mouse(0x0800, [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$step[3] * 120), 0))
        }
        'shot' {
            # The game's client area only, and only while the game is in front, so nothing else on screen is captured.
            Guard
            Add-Type -AssemblyName System.Drawing
            $r = New-Object W+RECT
            [void][W]::GetClientRect($hwnd, [ref]$r)
            $p = New-Object W+POINT
            [void][W]::ClientToScreen($hwnd, [ref]$p)
            $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($p.X, $p.Y, 0, 0, $bmp.Size)
            $bmp.Save($Steps[$i].Substring(5), [System.Drawing.Imaging.ImageFormat]::Png)
            $g.Dispose(); $bmp.Dispose()
            Write-Output "shot $($Steps[$i].Substring(5))"
        }
        default { Write-Output "error unknown step: $($Steps[$i])"; exit 1 }
    }
    $i++
}
Write-Output 'done'
