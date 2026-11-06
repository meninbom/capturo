# Captura as telas reais do Capturo para o vídeo, sobre um fundo controlado (bug.png / editor.png).
# Rodar com: powershell -STA -ExecutionPolicy Bypass -File capture.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CW {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[CW]::SetProcessDPIAware() | Out-Null
$here = $PSScriptRoot
$out = Join-Path (Split-Path $here) 'assets\ui'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path (Split-Path (Split-Path $here)) 'dist\Capturo.exe'
$ini = "$env:APPDATA\Capturo\settings.ini"

function Pump($ms) { $t = [DateTime]::UtcNow.AddMilliseconds($ms); while ([DateTime]::UtcNow -lt $t) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 15 } }
function Key($vk, [switch]$up) { [CW]::keybd_event($vk, 0, $(if ($up) { 2 } else { 0 }), [UIntPtr]::Zero) }
function Tap($vk) { Key $vk; Key $vk -up; Pump 120 }
function Down { [CW]::mouse_event(2,0,0,0,[UIntPtr]::Zero) }
function Up { [CW]::mouse_event(4,0,0,0,[UIntPtr]::Zero) }
function Drag($x0,$y0,$x1,$y1,$n=16) {
  [CW]::SetCursorPos($x0,$y0); Pump 80; Down
  for ($i = 1; $i -le $n; $i++) { [CW]::SetCursorPos([int]($x0+($x1-$x0)*$i/$n), [int]($y0+($y1-$y0)*$i/$n)); Pump 25 }
  Up; Pump 250
}
$ps = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
function Shot($name) {
  $b = New-Object System.Drawing.Bitmap $ps.Width, $ps.Height
  $g = [System.Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($ps.Left, $ps.Top, 0, 0, $ps.Size); $g.Dispose()
  $b.Save((Join-Path $out $name)); $b.Dispose(); "shot $name"
}

# Configurações controladas (cor vermelha padrão, traço 4, ícones 100%); restauradas no fim.
Copy-Item $ini "$ini.videobak" -Force
$cfg = (Get-Content $ini) | Where-Object { $_ -notmatch '^(Color|Thickness|IconScale)=' }
$cfg += 'Color=-1368526'; $cfg += 'Thickness=4'; $cfg += 'IconScale=100'
Set-Content $ini $cfg -Encoding UTF8

try {
  Get-Process Capturo -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Process $exe; Pump 1500

  $bg = New-Object System.Windows.Forms.Form
  $bg.FormBorderStyle = 'None'; $bg.StartPosition = 'Manual'; $bg.Bounds = $ps
  $bg.BackgroundImageLayout = 'Stretch'; $bg.ShowInTaskbar = $false
  $bg.BackgroundImage = [System.Drawing.Image]::FromFile((Join-Path $here 'bug.png'))
  $bg.Show(); $bg.Activate(); Pump 800
  Shot 'bug-screen.png'

  Start-Process $exe -ArgumentList '--capture'; Pump 1300
  [CW]::SetCursorPos(1300, 600); Pump 200
  [CW]::SetCursorPos(1720, 474); Pump 500
  Shot 'overlay-loupe.png'

  Drag 1150 440 1846 712
  [CW]::SetCursorPos(900, 950); Pump 400
  Shot 'overlay-selected.png'

  Tap 0x41                                     # A = seta
  Drag 1500 640 1700 495 12
  Tap 0x54                                     # T = texto
  [CW]::SetCursorPos(1196, 600); Pump 80; Down; Up; Pump 250
  [System.Windows.Forms.SendKeys]::SendWait("aqui quebra"); Pump 300
  Tap 0x1B                                     # Esc conclui o texto
  [CW]::SetCursorPos(900, 950); Pump 400
  Shot 'overlay-annotated.png'

  Key 0x11; Tap 0x54; Key 0x11 -up; Pump 900   # Ctrl+T = fixar
  $bg.BackgroundImage = [System.Drawing.Image]::FromFile((Join-Path $here 'editor.png')); $bg.Invalidate(); Pump 600
  [CW]::SetCursorPos(900, 950); Pump 300
  Shot 'pinned.png'

  [CW]::SetCursorPos(1500, 560); Pump 150; Down; Up; Pump 200   # foco no pin
  [System.Windows.Forms.Clipboard]::Clear()
  Key 0x11; Tap 0x43; Key 0x11 -up; Pump 800   # Ctrl+C no pin
  $img = [System.Windows.Forms.Clipboard]::GetImage()
  if ($img) { $img.Save((Join-Path $out 'clip.png')); "clip $($img.Width)x$($img.Height)" } else { "SEM CLIPBOARD" }

  [CW]::mouse_event(8,0,0,0,[UIntPtr]::Zero); [CW]::mouse_event(16,0,0,0,[UIntPtr]::Zero); Pump 700   # menu do pin
  Shot 'pinned-menu.png'
  Tap 0x1B; Pump 200; Tap 0x1B; Pump 300
  $bg.Close()
}
finally {
  Get-Process Capturo -ErrorAction SilentlyContinue | Stop-Process -Force
  Move-Item -Force "$ini.videobak" $ini
  Start-Process $exe
  Get-Content "$env:APPDATA\Capturo\log.txt" -Tail 5
}
