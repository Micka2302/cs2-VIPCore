#!/usr/bin/env pwsh
param(
    [switch]$FailOnModuleError
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$solution = Join-Path $root 'VIPCore/VIPCore.sln'
$mainProject = Join-Path $root 'VIPCore/VIPCore/VIPCore.csproj'
$modulesRoot = Join-Path $root 'VIPCore/modules'

$compiledRoot = Join-Path $root 'compiled'
$mainStageRoot = Join-Path $compiledRoot 'main'
$modulesStageRoot = Join-Path $compiledRoot 'modules'
$mainPluginName = 'VIPCore'
$mainPluginTarget = Join-Path $mainStageRoot "addons/counterstrikesharp/plugins/$mainPluginName"
$mainPluginLangSource = Join-Path $root 'VIPCore/VIPCore/lang'
$mainPluginLangTarget = Join-Path $mainPluginTarget 'lang'
$extraPluginDependencyNames = @('MySqlConnector.dll', 'Dapper.dll', 'Drapper.dll')
$extraPluginDependencySearchRoots = @($root, (Join-Path $root 'VIPCore'))

function Ensure-Folder {
    param([Parameter(Mandatory = $true)][string]$Path)
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Strip-PluginRuntimeFiles {
    param([Parameter(Mandatory = $true)][string]$PluginFolder)

    $runtimeDir = Join-Path $PluginFolder 'runtimes'
    if (Test-Path $runtimeDir) {
        $keep = @('linux-x64', 'win-x64')
        Get-ChildItem $runtimeDir -Directory |
            Where-Object { $keep -notcontains $_.Name } |
            Remove-Item -Recurse -Force
    }

    $cssApi = Join-Path $PluginFolder 'CounterStrikeSharp.API.dll'
    if (Test-Path $cssApi) {
        Remove-Item $cssApi -Force
    }
}

function Build-And-Stage {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [Parameter(Mandatory = $true)][string]$TargetFolder
    )

    $buildOutput = & dotnet build $ProjectPath -c Release --nologo 2>&1
    $buildOutput | ForEach-Object { Write-Host $_ }

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $ProjectPath"
    }

    $outputDir = $null
    $artifactRegex = [regex]'->\s+(?<path>.+\.dll)\s*$'
    foreach ($line in $buildOutput) {
        $text = [string]$line
        $match = $artifactRegex.Match($text)
        if ($match.Success) {
            $artifactPath = $match.Groups['path'].Value.Trim()
            if (Test-Path $artifactPath) {
                $outputDir = Split-Path -Parent $artifactPath
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($outputDir)) {
        $projectDir = Split-Path -Parent $ProjectPath
        $outputDir = Join-Path $projectDir 'bin/Release/net8.0'
    }

    if (-not (Test-Path $outputDir)) {
        throw "Build output not found at $outputDir"
    }

    Ensure-Folder -Path $TargetFolder
    Copy-Item -Path (Join-Path $outputDir '*') -Destination $TargetFolder -Recurse -Force
    Strip-PluginRuntimeFiles -PluginFolder $TargetFolder
}

function Copy-ExtraPluginDependencies {
    param([Parameter(Mandatory = $true)][string]$TargetFolder)

    foreach ($dependencyName in $extraPluginDependencyNames) {
        $sourcePath = $null
        foreach ($searchRoot in $extraPluginDependencySearchRoots) {
            $candidate = Join-Path $searchRoot $dependencyName
            if (Test-Path $candidate) {
                $sourcePath = $candidate
                break
            }
        }

        if ($null -ne $sourcePath) {
            Copy-Item -Path $sourcePath -Destination (Join-Path $TargetFolder $dependencyName) -Force
            Write-Host "  [OK] Extra dependency copied: $dependencyName"
        }
        else {
            Write-Host "  [WARN] Extra dependency not found: $dependencyName"
        }
    }
}

Write-Host '[INFO] Cleaning output folders...'
Remove-Item -Recurse -Force $compiledRoot -ErrorAction SilentlyContinue
Ensure-Folder -Path $mainPluginTarget
Ensure-Folder -Path $modulesStageRoot

Write-Host '[INFO] Restoring solution packages...'
dotnet restore $solution

Write-Host '[INFO] Building main plugin...'
Build-And-Stage -ProjectPath $mainProject -TargetFolder $mainPluginTarget

if (Test-Path $mainPluginLangSource) {
    Ensure-Folder -Path $mainPluginLangTarget
    Copy-Item -Path (Join-Path $mainPluginLangSource '*') -Destination $mainPluginLangTarget -Recurse -Force
    Write-Host "  [OK] Main plugin lang copied to $mainPluginLangTarget"
}
else {
    Write-Host "  [WARN] Main plugin lang folder not found: $mainPluginLangSource"
}

Copy-ExtraPluginDependencies -TargetFolder $mainPluginTarget

Write-Host '[INFO] Building modules...'
$moduleProjects = Get-ChildItem $modulesRoot -Recurse -File -Filter '*.csproj' | Sort-Object FullName
$moduleSuccess = @()
$moduleFailures = @()

foreach ($moduleProject in $moduleProjects) {
    $moduleName = [System.IO.Path]::GetFileNameWithoutExtension($moduleProject.Name)
    $moduleTarget = Join-Path $modulesStageRoot "addons/counterstrikesharp/plugins/$moduleName"

    try {
        Build-And-Stage -ProjectPath $moduleProject.FullName -TargetFolder $moduleTarget
        $moduleSuccess += $moduleName
        Write-Host "  [OK] $moduleName"
    }
    catch {
        $moduleFailures += [PSCustomObject]@{
            Name = $moduleName
            Project = $moduleProject.FullName
            Error = $_.Exception.Message
        }
        Write-Host "  [FAIL] $moduleName"
    }
}

$mainZipPath = Join-Path $compiledRoot "$mainPluginName-main.zip"
$modulesZipPath = Join-Path $compiledRoot "$mainPluginName-modules.zip"

if (Test-Path $mainZipPath) {
    Remove-Item $mainZipPath -Force
}
if (Test-Path $modulesZipPath) {
    Remove-Item $modulesZipPath -Force
}

Compress-Archive -Path (Join-Path $mainStageRoot '*') -DestinationPath $mainZipPath
Compress-Archive -Path (Join-Path $modulesStageRoot '*') -DestinationPath $modulesZipPath

Write-Host ''
Write-Host '[OK] Build finished.'
Write-Host " - Main plugin folder:  $mainPluginTarget"
Write-Host " - Modules folder:      $modulesStageRoot"
Write-Host " - Main plugin zip:     $mainZipPath"
Write-Host " - Modules zip:         $modulesZipPath"
Write-Host " - Modules compiled:    $($moduleSuccess.Count)"
Write-Host " - Modules failed:      $($moduleFailures.Count)"

if ($moduleFailures.Count -gt 0) {
    Write-Host ''
    Write-Host '[WARN] Some modules failed to compile:'
    foreach ($failure in $moduleFailures) {
        Write-Host " - $($failure.Name): $($failure.Error)"
        Write-Host "   Project: $($failure.Project)"
    }

    if ($FailOnModuleError) {
        throw 'Compilation failed for one or more modules.'
    }
}
