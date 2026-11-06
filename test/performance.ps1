param([ValidateSet('GifPerformance','PreviewPerformance','OcrSelectionSmoke')][string]$Suite = 'PreviewPerformance')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force $out | Out-Null
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wm = "$env:WINDIR\System32\WinMetadata"
$refs = @('/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', "/r:$fw\System.Runtime.dll", "/r:$fw\System.Runtime.WindowsRuntime.dll")
$refs += @('Windows.Foundation','Windows.Media','Windows.Graphics','Windows.Storage','Windows.Globalization' | ForEach-Object { "/r:$wm\$_.winmd" })
$test = switch ($Suite) {
    GifPerformance { 'gif-performance.cs' }
    PreviewPerformance { 'preview-performance.cs' }
    OcrSelectionSmoke { 'ocr-selection.cs' }
}
$sources = @(Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object FullName) + (Join-Path $PSScriptRoot $test)
$exe = Join-Path $out "$Suite.exe"
& "$fw\csc.exe" /nologo /unsafe /optimize+ /target:exe "/main:$Suite" "/out:$exe" @refs @sources
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& $exe (Join-Path $out 'after.gif')
if ($LASTEXITCODE -ne 0) { throw "$Suite failed" }
