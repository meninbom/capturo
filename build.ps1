# Compila o Capturo usando o compilador C# que já vem no Windows (.NET Framework 4.x).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out  = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $out | Out-Null

$common = @('/nologo', '/optimize+', '/target:winexe', '/platform:anycpu',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
$src = Join-Path $root 'src\Program.cs'

# 1ª passada: gera o .exe para que ele mesmo grave o ícone
& $csc @common "/out:$out\Capturo.exe" $src
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação' }
& "$out\Capturo.exe" --write-icon "$out\Capturo.ico" | Out-Null
Start-Sleep -Milliseconds 300

# 2ª passada: embute o ícone no .exe
& $csc @common "/win32icon:$out\Capturo.ico" "/out:$out\Capturo.exe" $src
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação' }
Write-Host "OK -> $out\Capturo.exe"
