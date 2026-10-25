param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore CrushKit.slnx --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    foreach ($name in @('Draw2D', 'Vulkan', 'ImGui', 'Input', 'Audio', 'Ecs', 'Physics')) {
        dotnet pack "src/CrushKit.Brutal.$name/CrushKit.Brutal.$name.csproj" -c $Configuration --no-restore -o artifacts/packages
        if ($LASTEXITCODE) { throw "Pack failed: $name" }
    }
} finally { Pop-Location }
