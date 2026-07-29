# Driver for launching, screenshotting, and clicking around the Intercom
# WinUI 3 desktop app via Windows UI Automation. See SKILL.md for usage.
#
# Usage:
#   pwsh .\driver.ps1 launch                     # start (or attach to) the app, print PID
#   pwsh .\driver.ps1 tree                        # dump the UIA control tree (name/type/AutomationId)
#   pwsh .\driver.ps1 screenshot <out.png>        # screenshot just the app window
#   pwsh .\driver.ps1 click "<selector>"          # invoke a button/control
#   pwsh .\driver.ps1 type "<selector>" "<text>"  # set the value of an edit control
#   pwsh .\driver.ps1 close                       # close the window (Alt+F4 equivalent)
#
# <selector> matches a control's UIA Name by default. Several controls (e.g.
# per-card "Ack" buttons) share a Name across instances or have dynamic/emoji
# Names not worth typing — prefix with "id:" to match AutomationId instead,
# e.g. click "id:ChatSendButton". Run 'tree' to see both Name and
# AutomationId for every control.

param(
    [Parameter(Mandatory = $true, Position = 0)] [string]$Command,
    [Parameter(Position = 1)] [string]$Arg1,
    [Parameter(Position = 2)] [string]$Arg2
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class IntercomDriverWin32 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$AppUserModelId = "Allann.Intercom_azfvp4fdjgk16!App"
$ProcessName = "Intercom.App"

function Get-IntercomProcess {
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}

function Get-IntercomWindowElement {
    $proc = Get-IntercomProcess
    if (-not $proc) { throw "Intercom.App is not running with a visible window. Run 'launch' first." }
    $elem = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    if (-not $elem) { throw "Could not get an AutomationElement for the Intercom window." }
    return $elem
}

function Find-IntercomControl($root, [string]$selector) {
    if ($selector.StartsWith("id:")) {
        $prop = [System.Windows.Automation.AutomationElement]::AutomationIdProperty
        $value = $selector.Substring(3)
    } else {
        $prop = [System.Windows.Automation.AutomationElement]::NameProperty
        $value = $selector
    }
    $cond = New-Object System.Windows.Automation.PropertyCondition($prop, $value)
    $target = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $target) { throw "No control matching '$selector' found. Run 'tree' to list Name/AutomationId values." }
    return $target
}

switch ($Command) {

    "launch" {
        $existing = Get-IntercomProcess
        if ($existing) {
            Write-Output "Already running: PID $($existing.Id)"
            return
        }
        Start-Process "shell:AppsFolder\$AppUserModelId"
        $deadline = (Get-Date).AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 500
            $proc = Get-IntercomProcess
        } while (-not $proc -and (Get-Date) -lt $deadline)
        if (-not $proc) { throw "Intercom.App did not present a window within 15s." }
        [IntercomDriverWin32]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
        [IntercomDriverWin32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
        Write-Output "Launched: PID $($proc.Id)"
    }

    "tree" {
        $root = Get-IntercomWindowElement
        function Write-Node($el, $depth) {
            $indent = "  " * $depth
            $ctrlType = $el.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
            Write-Output "$indent[$ctrlType] Name='$($el.Current.Name)' AutomationId='$($el.Current.AutomationId)'"
            $children = $el.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($c in $children) { Write-Node $c ($depth + 1) }
        }
        Write-Node $root 0
    }

    "screenshot" {
        if (-not $Arg1) { throw "Usage: driver.ps1 screenshot <out.png>" }
        $proc = Get-IntercomProcess
        if (-not $proc) { throw "Intercom.App is not running with a visible window. Run 'launch' first." }
        [IntercomDriverWin32]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
        [IntercomDriverWin32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 500
        $rect = New-Object IntercomDriverWin32+RECT
        [IntercomDriverWin32]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
        $width = $rect.Right - $rect.Left
        $height = $rect.Bottom - $rect.Top
        $bmp = New-Object System.Drawing.Bitmap $width, $height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
        $bmp.Save($Arg1, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "Saved $Arg1 ($width x $height)"
    }

    "click" {
        if (-not $Arg1) { throw "Usage: driver.ps1 click ""<selector>""" }
        $root = Get-IntercomWindowElement
        $target = Find-IntercomControl $root $Arg1
        $invoke = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        Write-Output "Clicked '$Arg1'"
    }

    "type" {
        if (-not $Arg1) { throw "Usage: driver.ps1 type ""<selector>"" ""<text>""" }
        $root = Get-IntercomWindowElement
        $target = Find-IntercomControl $root $Arg1
        $valuePattern = $target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $valuePattern.SetValue($Arg2)
        Write-Output "Set '$Arg1' to '$Arg2'"
    }

    "close" {
        $proc = Get-IntercomProcess
        if ($proc) {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
            $window = $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $window.Close()
            Write-Output "Closed PID $($proc.Id)"
        } else {
            Write-Output "Not running."
        }
    }

    default {
        throw "Unknown command '$Command'. Use: launch | tree | screenshot | click | type | close"
    }
}
