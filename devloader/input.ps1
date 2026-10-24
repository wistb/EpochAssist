# Sends real keyboard and mouse input to the game window, for in-game checks driven from WSL (offline only).
# Dev only; never shipped. Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File devloader/input.ps1 <step> [<step>...]
# Steps:  focus | restore | wait <ms> | key <Key> [ms] | down <Key> | up <Key> | type <text>
#         click <x> <y> [left|right|middle|x1|x2] | press <x> <y> <button> <ms> | move <x> <y>
#         (x, y: pixels in the game window's client area, top-left origin; Key: a System.Windows.Forms.Keys name such as
#         F2, Q, Return, Escape, LControlKey, RControlKey, LShiftKey, Menu)
# "focus" brings the game to the front; "restore" gives focus back to the window that had it before.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Steps)
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

    public static void Mouse(uint flags, uint data) {
        var i = new INPUT[1];
        i[0].type = 0;
        i[0].u.mi.flags = flags;
        i[0].u.mi.data = data;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@

$game = Get-Process 'Last Epoch' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $game) { Write-Output 'error no game window'; exit 1 }
$hwnd = $game.MainWindowHandle
$previous = [W]::GetForegroundWindow()
$prevFile = Join-Path $env:TEMP 'epochassist-input-previous.txt'

# Keys whose scancode needs the extended flag.
$extended = 'RControlKey', 'RMenu', 'Insert', 'Delete', 'Home', 'End', 'PageUp', 'PageDown', 'Up', 'Down', 'Left', 'Right', 'Divide', 'NumLock'

function Vk([string]$name) { [uint16][System.Windows.Forms.Keys]$name }
function KeyEvent([string]$name, [bool]$up) { [W]::Key((Vk $name), $up, $extended -contains $name) }

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
    $p = New-Object W+POINT
    $p.X = $x; $p.Y = $y
    [void][W]::ClientToScreen($hwnd, [ref]$p)
    [void][W]::SetCursorPos($p.X, $p.Y)
}

$buttons = @{ left = @(0x0002, 0x0004, 0); right = @(0x0008, 0x0010, 0); middle = @(0x0020, 0x0040, 0); x1 = @(0x0080, 0x0100, 1); x2 = @(0x0080, 0x0100, 2) }

$i = 0
while ($i -lt $Steps.Count) {
    $step = $Steps[$i] -split ' '
    switch ($step[0]) {
        'focus' {
            # Remember who had focus, across calls, so a later "restore" can give it back.
            if ($previous -ne $hwnd) { Set-Content -Path $prevFile -Value ([int64]$previous) }
            Write-Output "focus $(Focus $hwnd)"
        }
        'restore' {
            $back = if (Test-Path $prevFile) { [IntPtr][int64](Get-Content $prevFile) } else { $previous }
            if ($back -ne $hwnd -and $back -ne [IntPtr]::Zero) { Write-Output "restore $(Focus $back)" }
        }
        'wait' { Start-Sleep -Milliseconds ([int]$step[1]) }
        'down' { KeyEvent $step[1] $false }
        'up' { KeyEvent $step[1] $true }
        'key' {
            $hold = if ($step.Count -gt 2) { [int]$step[2] } else { 60 }
            KeyEvent $step[1] $false; Start-Sleep -Milliseconds $hold; KeyEvent $step[1] $true; Start-Sleep -Milliseconds 40
        }
        'type' {
            foreach ($c in ($Steps[$i].Substring(5)).ToCharArray()) {
                $name = if ($c -eq ' ') { 'Space' } elseif ([char]::IsDigit($c)) { "D$c" } else { [string]([char]::ToUpper($c)) }
                KeyEvent $name $false; Start-Sleep -Milliseconds 30; KeyEvent $name $true; Start-Sleep -Milliseconds 40
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
        default { Write-Output "error unknown step: $($Steps[$i])"; exit 1 }
    }
    $i++
}
Write-Output 'done'
