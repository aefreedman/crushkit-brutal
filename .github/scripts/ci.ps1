param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Check([string]$stage) { if ($LASTEXITCODE -ne 0) { throw "$stage failed (exit $LASTEXITCODE)." } }
Push-Location $root
try {
    dotnet restore CrushKit.slnx --locked-mode
    Check 'Locked restore'
    dotnet build CrushKit.slnx -c $Configuration --no-restore
    Check 'Build'
    foreach ($name in @('Draw2D', 'Vulkan', 'ImGui', 'Input', 'Audio', 'Ecs', 'Physics')) {
        dotnet test "tests/$name/$name.Tests.csproj" -c $Configuration --no-build --no-restore
        Check "$name headless tests"
    }
} finally { Pop-Location }
