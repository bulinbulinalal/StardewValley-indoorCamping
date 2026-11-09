param([string]$GamePath = 'D:\steam\steamapps\common\Stardew Valley',
    [string]$CompilerPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'dist/tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$refs = Get-ChildItem -LiteralPath $GamePath -Filter '*.dll' | Where-Object {
    try { [Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null; $true } catch { $false }
}
$options = @('/nologo', '/target:exe', '/langversion:latest', '/nullable:enable', '/nostdlib+', "/out:$output/Smoke.dll")
$options += $refs | ForEach-Object { '/reference:' + $_.FullName }
$options += '/reference:' + (Join-Path $GamePath 'smapi-internal/0Harmony.dll')
$options += '/reference:' + (Join-Path $GamePath 'smapi-internal/Newtonsoft.Json.dll')
$options += Join-Path $PSScriptRoot 'Smoke.cs'
& $CompilerPath @options
if ($LASTEXITCODE -ne 0) { throw 'Smoke test compilation failed.' }
'{"runtimeOptions":{"tfm":"net7.0","framework":{"name":"Microsoft.NETCore.App","version":"7.0.0"},"rollForward":"LatestMajor"}}' | Set-Content (Join-Path $output 'Smoke.runtimeconfig.json')
& dotnet (Join-Path $output 'Smoke.dll') $GamePath (Join-Path $root 'dist/IndoorCamping')
if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
