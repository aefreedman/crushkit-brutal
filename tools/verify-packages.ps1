param([string]$Configuration = 'Release', [switch]$HiddenSmoke, [string]$TextFont)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# Resolve application-supplied assets against caller cwd before switching directories.
if ($TextFont) { $TextFont = (Resolve-Path $TextFont).Path; if (!$HiddenSmoke) { throw '-TextFont requires -HiddenSmoke.' } }
function Check-Exit([string]$stage) { if ($LASTEXITCODE) { throw "$stage failed (exit $LASTEXITCODE)." } }
function Assert-File([string]$rootPath, [string]$relative) { if (!(Test-Path (Join-Path $rootPath $relative))) { throw "Missing output: $relative" } }
Push-Location $root
try {
    & "$PSScriptRoot/pack-local.ps1" -Configuration $Configuration
    $versions = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup
    $version = $versions.Version
    $imguiVersion = $versions.ImGuiVersion
    $packages = Join-Path $root 'artifacts/packages'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($name in @('Draw2D', 'Vulkan', 'ImGui', 'Input', 'Audio', 'Ecs', 'Physics')) {
        $packageVersion = if ($name -eq 'ImGui') { $imguiVersion } else { $version }
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $packages "CrushKit.Brutal.$name.$packageVersion.nupkg"))
        try {
            $entries = @($archive.Entries.FullName)
            foreach ($required in @("lib/net10.0/CrushKit.Brutal.$name.dll", "buildTransitive/CrushKit.Brutal.$name.targets")) {
                if ($required -notin $entries) { throw "Missing package entry: $required" }
            }
            if (!($entries | Where-Object { $_ -like 'notices/*/LICENSE.txt' })) { throw "Missing dependency licences: $name" }
            # Every pinned BRUTAL dependency must have distinct notices, including native closures.
            $lock = Get-Content "src/CrushKit.Brutal.$name/packages.lock.json" -Raw | ConvertFrom-Json -AsHashtable
            foreach ($id in $lock.dependencies.'net10.0'.Keys | Where-Object { $_ -like 'Ahwoo.BRUTAL.*' }) {
                $component = $id.Substring('Ahwoo.BRUTAL.'.Length)
                foreach ($file in @('LICENSE.txt', 'NOTICE.txt')) {
                    $entry = "notices/$component/$file"
                    # Project dependencies carry their own buildTransitive notice closure.
                    if ($entry -notin $entries -and $name -in @('Input', 'Audio', 'Physics')) { throw "Missing dependency notice: $name/$entry" }
                }
            }
        } finally { $archive.Dispose() }
    }
    # Fresh caches/lock files reject stale global-cache copies of unpublished previews.
    foreach ($kind in @('renderer', 'mechanics', 'ecs', 'physics')) {
        $probe = Join-Path $root ".local/package-consumer-$kind"
        if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
        New-Item $probe -ItemType Directory -Force | Out-Null
        if ($kind -eq 'renderer') {
            Copy-Item samples/ShapeGallery/Program.cs,samples/ShapeGallery/sample.json $probe
            $references = "<PackageReference Include='CrushKit.Brutal.Vulkan' Version='$version' />"
        } elseif ($kind -eq 'mechanics') {
            Copy-Item samples/InputAudio/Program.cs $probe
            $references = "<PackageReference Include='CrushKit.Brutal.Input' Version='$version' /><PackageReference Include='CrushKit.Brutal.Audio' Version='$version' />"
        } elseif ($kind -eq 'ecs') {
            Copy-Item samples/EcsJobs/Program.cs $probe
            $references = "<PackageReference Include='CrushKit.Brutal.Ecs' Version='$version' />"
        } else {
            Copy-Item samples/PhysicsJobs/Program.cs $probe
            $references = "<PackageReference Include='CrushKit.Brutal.Physics' Version='$version' />"
        }
        Copy-Item global.json $probe
        $project = Join-Path $probe 'PackageConsumer.csproj'
        "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup><ItemGroup>$references<None Update='sample.json' CopyToOutputDirectory='PreserveNewest' /></ItemGroup></Project>" | Set-Content $project
        $cache = Join-Path $probe 'packages'
        $config = Join-Path $probe 'nuget.config'
        $escapedFeed = [System.Security.SecurityElement]::Escape($packages)
        "<configuration><packageSources><clear/><add key='CrushKitLocal' value='$escapedFeed'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources><packageSourceMapping><packageSource key='CrushKitLocal'><package pattern='CrushKit.Brutal.*'/></packageSource><packageSource key='nuget.org'><package pattern='*'/></packageSource></packageSourceMapping></configuration>" | Set-Content $config
        dotnet restore $project --packages $cache --configfile $config; Check-Exit "$kind restore"
        dotnet restore $project --locked-mode --packages $cache --configfile $config; Check-Exit "$kind locked restore"
        dotnet run --project $project -c $Configuration --no-restore; Check-Exit "$kind contract"
        $assets = Get-Content (Join-Path $probe 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        $forbidden = if ($kind -eq 'renderer') { '(?i)imgui|opengl|sdl\.mixer' } elseif ($kind -eq 'mechanics') { '(?i)vulkan|imgui|opengl|shaderc|breakout' } elseif ($kind -eq 'ecs') { '(?i)vulkan|imgui|opengl|shaderc|breakout|ahwoo|glfw|sdl|box2d' } else { '(?i)vulkan|imgui|opengl|shaderc|breakout|glfw|sdl|^arch/' }
        if (@($assets.libraries.Keys | Where-Object { $_ -match $forbidden }).Count) { throw "$kind has unexpected dependencies." }
        $output = Join-Path $probe "bin/$Configuration/net10.0"
        if ($kind -eq 'renderer') {
            foreach ($file in @('CrushKit.Brutal.Vulkan.dll', 'CrushKit.Brutal.Draw2D.dll', 'Licenses/Media/Vulkan/LICENSE.txt', 'Licenses/Media/Glfw/NOTICE.txt')) { Assert-File $output $file }
            if (!(Get-ChildItem $output -Recurse -Filter '*shaderc*.dll')) { throw 'ShaderC native runtime did not propagate.' }
            if ($HiddenSmoke) {
                $smokeArgs = @('--hidden-smoke')
                if ($TextFont) { $smokeArgs += @('--text-font', $TextFont) }
                dotnet run --project $project -c $Configuration --no-build -- @smokeArgs; Check-Exit 'Hidden package-consumer smoke'
            }
        } elseif ($kind -eq 'mechanics') {
            foreach ($file in @('CrushKit.Brutal.Input.dll', 'CrushKit.Brutal.Audio.dll', 'Licenses/CrushKit/Audio/Sdl.Mixer/NOTICE.txt', 'Licenses/CrushKit/Input/Glfw/LICENSE.txt')) { Assert-File $output $file }
            foreach ($pattern in @('*SDL3_mixer*.dll', '*glfw*.dll')) {
                if (!(Get-ChildItem $output -Recurse -Filter $pattern)) { throw "Native runtime missing: $pattern" }
            }
        }
        if ($kind -eq 'ecs') {
            foreach ($file in @('CrushKit.Brutal.Ecs.dll', 'Arch.dll', 'Schedulers.dll', 'Licenses/CrushKit/Ecs/Arch/LICENSE.MD', 'Licenses/CrushKit/Ecs/Arch.LowLevel/LICENSE.MD', 'Licenses/CrushKit/Ecs/ZeroAllocJobScheduler/LICENSE.MD', 'Licenses/CrushKit/Ecs/ObjectPool/LICENSE.txt', 'Licenses/CrushKit/Ecs/Collections.Pooled/LICENSE.txt', 'Licenses/CrushKit/Ecs/CommunityToolkit.HighPerformance/License.md')) { Assert-File $output $file }
        }
        if ($kind -eq 'physics') {
            foreach ($file in @('CrushKit.Brutal.Physics.dll', 'Brutal.Box2D.dll', 'Schedulers.dll', 'Licenses/CrushKit/Physics/Box2D/LICENSE.txt', 'Licenses/CrushKit/Physics/Box2D.Native/NOTICE.txt', 'Licenses/CrushKit/Physics/ZeroAllocJobScheduler/LICENSE.MD')) { Assert-File $output $file }
        }
        $publish = Join-Path $probe 'publish'
        dotnet publish $project -c $Configuration --no-restore -o $publish; Check-Exit "$kind publish"
        $notice = if ($kind -eq 'renderer') { 'Licenses/Media/Vulkan/LICENSE.txt' } elseif ($kind -eq 'mechanics') { 'Licenses/CrushKit/Audio/Sdl.Mixer/NOTICE.txt' } elseif ($kind -eq 'ecs') { 'Licenses/CrushKit/Ecs/Arch/LICENSE.MD' } else { 'Licenses/CrushKit/Physics/ZeroAllocJobScheduler/LICENSE.MD' }
        Assert-File $publish $notice
        Write-Host "Package verification passed: $kind fresh consumer, native assets, notices and dependency isolation."
    }
} finally { Pop-Location }
