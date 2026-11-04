$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W]::SetProcessDPIAware() | Out-Null
$dir = Split-Path $PSScriptRoot
$exe = Join-Path $dir 'dist\Capturo.exe'
Get-Process Capturo -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$env:APPDATA\Capturo\log.txt" -ErrorAction SilentlyContinue
$p = Start-Process $exe -PassThru
Start-Sleep 2
function Key($vk, [switch]$up) { [W]::keybd_event($vk, 0, $(if ($up) { 2 } else { 0 }), [UIntPtr]::Zero) }
function Shot($name) {
  $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
  $b = New-Object System.Drawing.Bitmap $vs.Width, $vs.Height
  $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($vs.Left, $vs.Top, 0, 0, $vs.Size); $g.Dispose()
  $b.Save((Join-Path $PSScriptRoot $name)); $b.Dispose()
}

Key 0x2C; Key 0x2C -up      # Print Screen
Start-Sleep 1
$log = Get-Content "$env:APPDATA\Capturo\log.txt" -Raw
if ($log -notmatch 'captura') { "OVERLAY NAO APARECEU"; $log; Stop-Process $p; exit 1 }
"overlay ok (PrintScreen interceptado: " + ($log -match 'PrintScreen') + ")"

$ps = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x0 = $ps.Left + 300; $y0 = $ps.Top + 200; $x1 = $x0 + 600; $y1 = $y0 + 350
[W]::SetCursorPos($x0, $y0); [W]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
for ($i = 1; $i -le 10; $i++) { [W]::SetCursorPos($x0 + 60*$i, $y0 + 35*$i); Start-Sleep -Milliseconds 30 }
[W]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 300

Key 0x41; Key 0x41 -up       # A = seta
[W]::SetCursorPos($x0 + 80, $y0 + 280); [W]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
for ($i = 1; $i -le 10; $i++) { [W]::SetCursorPos($x0 + 80 + 35*$i, $y0 + 280 - 20*$i); Start-Sleep -Milliseconds 30 }
[W]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Key 0x54; Key 0x54 -up       # T = texto
[W]::SetCursorPos($x0 + 60, $y0 + 40); [W]::mouse_event(2,0,0,0,[UIntPtr]::Zero); [W]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
[System.Windows.Forms.SendKeys]::SendWait("Ola Capturo")
[W]::SetCursorPos($x1 + 20, $y1 - 60)   # hover na barra de ferramentas
Start-Sleep -Milliseconds 400
Shot 'overlay.png'

Key 0x1B; Key 0x1B -up       # Esc finaliza o texto
Key 0x11; Key 0x43; Key 0x43 -up; Key 0x11 -up   # Ctrl+C
Start-Sleep 1
$img = [System.Windows.Forms.Clipboard]::GetImage()
if ($img) { "clipboard: $($img.Width) x $($img.Height)"; $img.Save((Join-Path $PSScriptRoot 'clip.png')) } else { "SEM IMAGEM NO CLIPBOARD" }
Get-Content "$env:APPDATA\Capturo\log.txt"
Stop-Process $p
