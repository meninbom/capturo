# Compila o Capturo usando o compilador C# que já vem no Windows (.NET Framework 4.x).
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc  = "$fw\csc.exe"
$wm   = "$env:WINDIR\System32\WinMetadata"   # metadados do WinRT (OCR do Windows 10/11)
$out  = $OutputDirectory
New-Item -ItemType Directory -Force $out | Out-Null

$common = @('/nologo', '/optimize+', '/unsafe', '/target:winexe', '/platform:anycpu',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
            "/r:$fw\System.Runtime.dll", "/r:$fw\System.Runtime.WindowsRuntime.dll",
            "/r:$wm\Windows.Foundation.winmd", "/r:$wm\Windows.Media.winmd", "/r:$wm\Windows.Graphics.winmd",
            "/r:$wm\Windows.Storage.winmd", "/r:$wm\Windows.Globalization.winmd", "/r:$wm\Windows.AI.winmd")
$src = Join-Path $root 'src\*.cs'
# papéis de parede do editor de vídeo: vão embutidos no .exe (Capturo.wall.<nome>.jpg)
$common += @(Get-ChildItem (Join-Path $root 'assets\wallpapers\*.jpg') -ErrorAction SilentlyContinue |
             ForEach-Object { "/resource:$($_.FullName),Capturo.wall.$($_.Name)" })
# modelo que separa a pessoa do fundo na webcam (efeitos da câmera no editor)
$common += "/resource:$(Join-Path $root 'assets\models\selfie_segmentation.onnx'),Capturo.selfie_segmentation.onnx"

# 1ª passada: gera o .exe para que ele mesmo grave o ícone
& $csc @common "/out:$out\Capturo.exe" $src
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação' }
$iconProcess = Start-Process -FilePath "$out\Capturo.exe" -ArgumentList @('--write-icon', "`"$out\Capturo.ico`"") -Wait -PassThru -WindowStyle Hidden
if ($iconProcess.ExitCode -ne 0) { throw 'Falha ao gerar ícone' }

# 2ª passada: embute o ícone no .exe
& $csc @common "/win32icon:$out\Capturo.ico" "/out:$out\Capturo.exe" $src
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação' }
Write-Host "OK -> $out\Capturo.exe"
