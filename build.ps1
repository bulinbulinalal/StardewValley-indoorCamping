param(
    [string]$GamePath = 'D:\steam\steamapps\common\Stardew Valley',
    [string]$CompilerPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath (Join-Path $GamePath 'StardewModdingAPI.dll'))) { throw 'GamePath must contain Stardew Valley and SMAPI.' }
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Set CompilerPath to the Visual Studio Roslyn csc.exe compiler.' }
$output = Join-Path $PSScriptRoot 'dist/IndoorCamping'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references = Get-ChildItem -LiteralPath $GamePath -Filter '*.dll' | Where-Object {
    try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true } catch { $false }
}
$options = @('/nologo', '/target:library', '/langversion:latest', '/nullable:enable', '/nostdlib+', '/optimize+', '/deterministic+', '/define:DIRECT_BUILD', "/out:$output/IndoorCamping.dll")
$options += $references | ForEach-Object { '/reference:' + $_.FullName }
$options += '/reference:' + (Join-Path $GamePath 'smapi-internal/0Harmony.dll')
$options += '/reference:' + (Join-Path $GamePath 'smapi-internal/Newtonsoft.Json.dll')
$options += '/reference:' + (Join-Path $GamePath 'smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll')
$options += (Join-Path $PSScriptRoot 'IndoorCamping/ModEntry.cs')
& $CompilerPath @options
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'IndoorCamping/manifest.json'), (Join-Path $PSScriptRoot 'IndoorCamping/config.json') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'IndoorCamping/i18n') -Destination $output -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'tests/MANUAL.md') -Destination $output
Compress-Archive -Path $output -DestinationPath (Join-Path $PSScriptRoot 'dist/IndoorCamping-0.1.0.zip') -Force
Write-Output "Built $output"
