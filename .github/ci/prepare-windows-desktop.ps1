Add-Type -Namespace Native -Name Desk -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr lParam);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, System.IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT r);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetClassName(System.IntPtr hwnd, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] public static extern int GetWindowText(System.IntPtr hwnd, System.Text.StringBuilder s, int n);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hwnd, int cmd);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetDpiForSystem();
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, System.IntPtr value, uint winIni);
'@
function Get-TopWindows {
  $list = New-Object System.Collections.Generic.List[object]
  [void][Native.Desk]::EnumWindows({ param($h, $l)
    if ([Native.Desk]::IsWindowVisible($h)) {
      $r = New-Object Native.Desk+RECT; [void][Native.Desk]::GetWindowRect($h, [ref]$r)
      if (($r.R - $r.L) -gt 0 -and ($r.B - $r.T) -gt 0) {
        $c = New-Object System.Text.StringBuilder 256; [void][Native.Desk]::GetClassName($h, $c, 256)
        $t = New-Object System.Text.StringBuilder 256; [void][Native.Desk]::GetWindowText($h, $t, 256)
        $procId = [uint32]0; [void][Native.Desk]::GetWindowThreadProcessId($h, [ref]$procId)
        $name = (Get-Process -Id $procId -ErrorAction SilentlyContinue).ProcessName
        $list.Add([pscustomobject]@{ Handle = $h; Process = $name; Pid = $procId; Class = "$c"; Title = "$t"; Rect = "$($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)" })
      }
    }
    $true }, [IntPtr]::Zero)
  $list
}
function Save-Desktop([string]$name) {
  Add-Type -AssemblyName System.Windows.Forms, System.Drawing
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  New-Item -ItemType Directory -Force "$env:RUNNER_TEMP/od-test-logs" | Out-Null
  $bmp.Save("$env:RUNNER_TEMP/od-test-logs/$name.png")
}

"system DPI = $([Native.Desk]::GetDpiForSystem()) (96 = 100%), screen = $([Native.Desk]::GetSystemMetrics(0)) x $([Native.Desk]::GetSystemMetrics(1)), arch = $env:PROCESSOR_ARCHITECTURE, session = $((Get-Process -Id $PID).SessionId)"
"== visible top-level windows before"
Get-TopWindows | Format-Table Process, Pid, Class, Title, Rect -AutoSize | Out-String -Width 220
Save-Desktop 'desktop-as-provisioned'

# First-logon/privacy UI. The page is hosted by these processes; stop them and keep the
# experience from being offered again.
New-Item -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OOBE' -Name DisablePrivacyExperience -Value 1 -Type DWord
foreach ($name in 'WWAHost', 'CloudExperienceHostBroker', 'UserOOBEBroker', 'OOBENetworkConnectionFlow', 'msoobe') {
  Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object { "stopping $($_.ProcessName) ($($_.Id))"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}
# Closing the first-logon page drops the session onto an open Start menu over the middle of
# the screen (it opened a few seconds later and was not in the window list). It is a shell
# surface that cannot be minimised; Esc closes it and is harmless when nothing is open.
Add-Type -AssemblyName System.Windows.Forms
for ($i = 0; $i -lt 3; $i++) {
  Start-Sleep -Seconds 2
  Get-TopWindows | Where-Object { $_.Process -in 'StartMenuExperienceHost', 'SearchHost', 'ShellExperienceHost' } |
    ForEach-Object { "shell surface $($_.Process) '$($_.Title)' $($_.Rect)" }
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
}
# Whatever is still visible and is not the shell gets minimised (SW_MINIMIZE).
Get-TopWindows | Where-Object { $_.Process -ne 'explorer' -and $_.Class -notin 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'Progman', 'WorkerW' } |
  ForEach-Object { "minimising $($_.Process) '$($_.Title)' [$($_.Class)]"; [void][Native.Desk]::ShowWindow($_.Handle, 6) }

# windows-11-arm's WSL app runs `wsl.exe --update --confirm --prompt-before-exit` from
# backgroundTaskHost every 30 s, each time in a new Windows Terminal window that becomes the
# foreground window over the app under test (the failures named it: WindowsTerminal, title
# 'C:\Windows\system32\wsl.exe'). A watchdog for the rest of the job stops exactly that
# command and its window, and records what started it; the runner cleans it up with the job.
# Its log lands in the uploaded test-log directory. Other wsl.exe processes are left alone:
# the images' provisioning agent probes WSL with a windowless `wsl -u root -e ls`, which an
# earlier version of this watchdog stopped as well (provjobd.exe in the log).
# The retries come from the update never finishing: --prompt-before-exit leaves it waiting for
# a key, and stopping it counts as a failed update. Finish the update here, before the tests,
# so the background task has nothing left to do; the watchdog stays as the safety net and its
# log shows whether it still had to step in.
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $update = Start-Process wsl.exe -ArgumentList '--update', '--web-download' -WindowStyle Hidden -PassThru
  if ($update.WaitForExit(300000)) {
    "wsl --update finished with exit code $($update.ExitCode) after $([int]$sw.Elapsed.TotalSeconds) s"
  } else {
    "wsl --update did not finish within 300 s; stopping it"
    Stop-Process -Id $update.Id -Force -ErrorAction SilentlyContinue
  }
  wsl.exe --version 2>&1 | Out-String
}

$watchdog = Join-Path $env:RUNNER_TEMP 'desktop-watchdog.ps1'
$watchdogLog = Join-Path $env:RUNNER_TEMP 'od-test-logs/desktop-watchdog.log'
Set-Content -Path $watchdog -Value @'
param([string]$Log)
while ($true) {
  foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'wsl.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like '*--update*' })) {
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId = $($p.ParentProcessId)" -ErrorAction SilentlyContinue
    Add-Content $Log "$(Get-Date -Format HH:mm:ss.fff) stopping $($p.Name) ($($p.ProcessId)) '$($p.CommandLine)' started by $($parent.Name) ($($p.ParentProcessId)) '$($parent.CommandLine)'"
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
  }
  foreach ($t in @(Get-Process -Name WindowsTerminal -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*wsl*' })) {
    Add-Content $Log "$(Get-Date -Format HH:mm:ss.fff) closing WindowsTerminal ($($t.Id)) '$($t.MainWindowTitle)'"
    Stop-Process -Id $t.Id -Force -ErrorAction SilentlyContinue
  }
  Start-Sleep -Milliseconds 500
}
'@
Start-Process pwsh -WindowStyle Hidden -ArgumentList '-NoProfile', '-File', $watchdog, '-Log', $watchdogLog
Start-Sleep -Seconds 2
if (Test-Path $watchdogLog) { Get-Content $watchdogLog }

$full = 0; [void][Native.Desk]::SystemParametersInfo(0x0026, 0, [ref]$full, 0)
$set = [Native.Desk]::SystemParametersInfo(0x0025, 1, [IntPtr]::Zero, 3)
$after = 0; [void][Native.Desk]::SystemParametersInfo(0x0026, 0, [ref]$after, 0)
"drag full windows: was=$full set=$set now=$after"

Start-Sleep -Seconds 1
"== visible top-level windows after"
Get-TopWindows | Format-Table Process, Pid, Class, Title, Rect -AutoSize | Out-String -Width 220
Save-Desktop 'desktop-before-tests'
exit 0
