param([string]$Configuration = 'Release', [switch]$HiddenSmoke)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & "$PSScriptRoot/pack-local.ps1" -Configuration $Configuration
    $packages = Join-Path $root 'artifacts/packages'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($name in @('Draw2D', 'Vulkan', 'ImGui')) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $packages "CrushKit.Brutal.$name.0.1.0-preview.1.nupkg"))
        try {
            $entries = @($archive.Entries.FullName)
            foreach ($required in @("lib/net10.0/CrushKit.Brutal.$name.dll", "buildTransitive/CrushKit.Brutal.$name.targets")) {
                if ($required -notin $entries) { throw "Missing package entry: $required" }
            }
            if (!($entries | Where-Object { $_ -like 'notices/*/LICENSE.txt' })) { throw "Missing dependency licences: $name" }
        } finally { $archive.Dispose() }
    }
    # Fresh packages and lock file avoid accepting a stale global-cache copy of an unpublished prerelease.
    $probe = Join-Path $root '.local/package-consumer'
    if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
    New-Item $probe -ItemType Directory -Force | Out-Null
    Copy-Item samples/ShapeGallery/Program.cs,samples/ShapeGallery/sample.json $probe
    Copy-Item global.json $probe
    $project = Join-Path $probe 'PackageConsumer.csproj'
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile></PropertyGroup>
  <ItemGroup><PackageReference Include="CrushKit.Brutal.Vulkan" Version="0.1.0-preview.1" /><None Update="sample.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
</Project>
'@ | Set-Content $project
    $cache = Join-Path $probe 'packages'
    $config = Join-Path $probe 'nuget.config'
    $escapedFeed = [System.Security.SecurityElement]::Escape($packages)
    "<configuration><packageSources><clear/><add key='CrushKitLocal' value='$escapedFeed'/><add key='nuget.org' value='https://api.nuget.org/v3/index.json'/></packageSources></configuration>" | Set-Content $config
    dotnet restore $project --packages $cache --configfile $config
    if ($LASTEXITCODE) { throw 'Package consumer restore failed.' }
    dotnet restore $project --locked-mode --packages $cache --configfile $config
    if ($LASTEXITCODE) { throw 'Package consumer locked restore failed.' }
    dotnet run --project $project -c $Configuration --no-restore
    if ($LASTEXITCODE) { throw 'Package consumer contract failed.' }
    $assets = Get-Content (Join-Path $probe 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    if (@($assets.libraries.Keys | Where-Object { $_ -match '(?i)imgui' }).Count) { throw 'Vulkan-only consumer unexpectedly depends on ImGui.' }
    $output = Join-Path $probe "bin/$Configuration/net10.0"
    foreach ($file in @('CrushKit.Brutal.Vulkan.dll', 'CrushKit.Brutal.Draw2D.dll', 'Licenses/Media/Vulkan/LICENSE.txt', 'Licenses/Media/Glfw/NOTICE.txt')) {
        if (!(Test-Path (Join-Path $output $file))) { throw "Consumer missing $file" }
    }
    if (!(Get-ChildItem $output -Recurse -Filter '*shaderc*.dll')) { throw 'ShaderC native runtime did not propagate.' }
    if ($HiddenSmoke) {
        dotnet run --project $project -c $Configuration --no-build -- --hidden-smoke
        if ($LASTEXITCODE) { throw 'Hidden package-consumer smoke failed.' }
    }
    dotnet publish $project -c $Configuration --no-restore -o (Join-Path $probe 'publish')
    if ($LASTEXITCODE) { throw 'Package consumer publish failed.' }
    if (!(Test-Path (Join-Path $probe 'publish/Licenses/Media/Vulkan/LICENSE.txt'))) { throw 'Publish licence propagation failed.' }
    Write-Host 'Package verification passed: fresh consumer, shader resources, native runtime, notices, optional ImGui isolation.'
} finally { Pop-Location }
