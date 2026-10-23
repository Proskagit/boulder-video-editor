<#
.SYNOPSIS
    Builds the portable Windows folder of the editor (S1, DECISIONS.md D046): self-contained win-x64, an ordinary
    folder - no single-file, no trimming, no installer.

.DESCRIPTION
    Run from a clean checkout:  pwsh -File tools/portable/Publish-Portable.ps1
    The result is artifacts/portable/AiVideoEditor-<version>-win-x64 (and a .zip of it with -Zip):
      AiVideoEditor.exe, the .NET runtime and every dependency, appsettings.json,
      README.txt (the user's instruction), THIRD-PARTY-NOTICES.txt and licenses\ (generated from the packages actually
      published - not a legal review), docs\AI_MODEL.md, BUILD-INFO.txt (commit, SDK, runtime, the folder's size).
    FFmpeg is NOT included (D046 par. 2: only after its licence audit and the product owner's choice) unless
    -FfmpegDir names a local FFmpeg for a local technical check.
    The SDK is pinned by global.json (8.0.424, latest patch); every NuGet package by its exact version in the projects.

.PARAMETER Version
    The product version written into the assemblies and the folder's name.
.PARAMETER OutputRoot
    Where the folder is created (default: artifacts/portable under the repository).
.PARAMETER Zip
    Also packs the folder into a .zip next to it.
.PARAMETER FfmpegDir
    For a local technical check only: a folder holding ffmpeg.exe and ffprobe.exe (a bin folder, or a distribution
    folder with bin\). They are copied into ffmpeg\ with the distribution's LICENSE / README into licenses\FFmpeg\,
    appsettings.json points at them by relative paths, and the notices name them as not reviewed. Not the chosen
    distribution of D046 par. 2 - nothing is decided by using it.
#>
[CmdletBinding()]
param(
    [string] $Version = '0.30.0',
    [string] $OutputRoot,
    [switch] $Zip,
    [string] $FfmpegDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$templates = Join-Path $PSScriptRoot 'files'
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\portable' }
$rid = 'win-x64'
$name = "AiVideoEditor-$Version-$rid"
$out = Join-Path $OutputRoot $name

function Invoke-Native([string] $file, [string[]] $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') failed ($LASTEXITCODE)" }
}

# --- The source state -------------------------------------------------------------------------------------------
Push-Location $repo
try {
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'git rev-parse failed - run from a git checkout' }
    $status = (& git status --porcelain --untracked-files=no)
    $dirty = [bool]$status
    $sdk = (& dotnet --version).Trim()
} finally {
    Pop-Location
}
$shortCommit = $commit.Substring(0, 10)
$informational = "$Version+$shortCommit" + $(if ($dirty) { '.dirty' } else { '' })
if ($dirty) { Write-Warning 'The working tree has uncommitted changes: the build is marked .dirty and is not reproducible from a commit.' }
Write-Host "Commit $commit$(if ($dirty) { ' (dirty)' }), SDK $sdk, version $informational"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

# --- Publish ----------------------------------------------------------------------------------------------------
$project = Join-Path $repo 'src\App\App.csproj'
Invoke-Native 'dotnet' @(
    'publish', $project,
    '-c', 'Release',
    '-r', $rid,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    "-p:Version=$Version",
    "-p:InformationalVersion=$informational",
    '-p:ContinuousIntegrationBuild=true',
    '-p:DebugType=none',
    '-o', $out,
    '-nologo'
)

# --- The runtime and the packages actually published (from the deps file) --------------------------------------
$depsFile = Join-Path $out 'AiVideoEditor.deps.json'
$deps = Get-Content -Raw -Encoding UTF8 $depsFile | ConvertFrom-Json
$packagesRoot = (& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$packagesRoot = $packagesRoot.Trim()

$components = @()
foreach ($property in $deps.libraries.PSObject.Properties) {
    $library = $property.Value
    if ($library.type -notin @('package', 'runtimepack')) { continue }
    $id, $libVersion = $property.Name -split '/', 2
    $id = $id -replace '^runtimepack\.', ''
    $folder = Join-Path $packagesRoot (Join-Path $id.ToLowerInvariant() $libVersion)
    $components += [pscustomobject]@{ Id = $id; Version = $libVersion; Folder = $folder }
}

# Only the packages that put files into the folder (runtime / native assets), not the compile-time-only ones.
$target = $deps.targets.PSObject.Properties | Where-Object { $_.Name -like "*/$rid" } | Select-Object -First 1
if (-not $target) { throw "No $rid target in $depsFile" }
$shipped = @{}
foreach ($entry in $target.Value.PSObject.Properties) {
    $assets = $entry.Value
    $hasFiles = $false
    foreach ($kind in 'runtime', 'native', 'resources') {
        if ($assets.PSObject.Properties[$kind] -and $assets.$kind) { $hasFiles = $true }
    }
    if ($hasFiles) { $shipped[($entry.Name -replace '^runtimepack\.', '')] = $true }
}
$components = @($components | Where-Object { $shipped.ContainsKey("$($_.Id)/$($_.Version)") } | Sort-Object Id)

function Get-NuspecInfo($component) {
    $nuspec = Get-ChildItem -Path $component.Folder -Filter '*.nuspec' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $nuspec) { return [pscustomobject]@{ License = '(package metadata not found)'; Project = ''; Copyright = '' } }
    [xml] $xml = Get-Content -Raw -Encoding UTF8 $nuspec.FullName
    $metadata = $xml.package.metadata
    $license = ''
    if ($metadata.PSObject.Properties['license'] -and $metadata.license) {
        $node = $metadata.license
        $license = if ($node -is [string]) { $node } else { "$($node.'#text') ($($node.type))" }
    } elseif ($metadata.PSObject.Properties['licenseUrl']) {
        $license = "see $($metadata.licenseUrl)"
    }
    [pscustomobject]@{
        License   = $license
        Project   = $(if ($metadata.PSObject.Properties['projectUrl']) { $metadata.projectUrl } else { '' })
        Copyright = $(if ($metadata.PSObject.Properties['copyright']) { $metadata.copyright } else { '' })
    }
}

# --- Licences and notices ---------------------------------------------------------------------------------------
$licenses = Join-Path $out 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($component in $components) {
    $info = Get-NuspecInfo $component
    $copied = @()
    if (Test-Path $component.Folder) {
        $files = Get-ChildItem -Path $component.Folder -File |
            Where-Object { $_.Name -match '^(LICENSE|LICENCE|NOTICE|THIRD-?PARTY-?NOTICES|ThirdPartyNotices)' }
        if ($files) {
            $dest = Join-Path $licenses $component.Id
            New-Item -ItemType Directory -Force $dest | Out-Null
            foreach ($file in $files) {
                Copy-Item $file.FullName (Join-Path $dest $file.Name)
                $copied += "licenses\$($component.Id)\$($file.Name)"
            }
        }
    }
    $lines.Add("$($component.Id) $($component.Version)")
    $lines.Add("  Licence (package metadata): $($info.License)")
    if ($info.Copyright) { $lines.Add("  Copyright: $($info.Copyright)") }
    if ($info.Project) { $lines.Add("  Project: $($info.Project)") }
    $lines.Add("  Licence files: $(if ($copied) { $copied -join ', ' } else { '(none in the package - see the licence above)' })")
    $lines.Add('')
}

# --- FFmpeg for a local technical check (-FfmpegDir) ------------------------------------------------------------------
$ffmpegLine = 'FFmpeg: not included (DECISIONS.md D046 par. 2).'
if ($FfmpegDir) {
    $bin = if (Test-Path (Join-Path $FfmpegDir 'bin\ffmpeg.exe')) { Join-Path $FfmpegDir 'bin' } else { $FfmpegDir }
    $distribution = if ($bin -ne $FfmpegDir) { $FfmpegDir } else { Split-Path $bin }
    $ffmpegOut = Join-Path $out 'ffmpeg'
    New-Item -ItemType Directory -Force $ffmpegOut | Out-Null
    foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
        $source = Join-Path $bin $exe
        if (-not (Test-Path $source)) { throw "-FfmpegDir: $source not found" }
        Copy-Item $source (Join-Path $ffmpegOut $exe)
    }
    $ffmpegVersion = (& (Join-Path $ffmpegOut 'ffmpeg.exe') -hide_banner -version | Select-Object -First 1)
    $hashes = foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
        "$exe SHA-256 $((Get-FileHash -Algorithm SHA256 (Join-Path $ffmpegOut $exe)).Hash.ToLowerInvariant())"
    }
    $licenceFiles = @(Get-ChildItem -Path $distribution -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|README|COPYING)' })
    if ($licenceFiles) {
        $dest = Join-Path $licenses 'FFmpeg'
        New-Item -ItemType Directory -Force $dest | Out-Null
        foreach ($file in $licenceFiles) { Copy-Item $file.FullName (Join-Path $dest $file.Name) }
    }
    $lines.Add("FFmpeg / FFprobe - $ffmpegVersion")
    $lines.Add("  In ffmpeg\ for a LOCAL TECHNICAL CHECK ONLY: not the chosen distribution (DECISIONS.md D046 par. 2), its licence")
    $lines.Add('  and obligations NOT reviewed; do not distribute this folder.')
    $lines.Add("  Source: $(Split-Path -Leaf $distribution) (a local folder given by -FfmpegDir)")
    foreach ($hash in $hashes) { $lines.Add("  $hash") }
    $lines.Add("  Licence files: $(if ($licenceFiles) { ($licenceFiles | ForEach-Object { "licenses\FFmpeg\$($_.Name)" }) -join ', ' } else { '(none found next to the binaries)' })")
    $lines.Add('')

    # The editor reads the tools' paths from appsettings.json; relative ones are resolved against its folder (D046 par. 1.2).
    $settingsPath = Join-Path $out 'appsettings.json'
    $settings = Get-Content -Raw -Encoding UTF8 $settingsPath | ConvertFrom-Json
    $settings.Ffmpeg.FfmpegPath = 'ffmpeg\ffmpeg.exe'
    $settings.Ffmpeg.FfprobePath = 'ffmpeg\ffprobe.exe'
    Set-Content -Path $settingsPath -Value ($settings | ConvertTo-Json -Depth 10) -Encoding UTF8
    $ffmpegLine = "FFmpeg: $ffmpegVersion in ffmpeg\ - local technical check only, not reviewed for distribution."
    Write-Host "FFmpeg: $ffmpegVersion from $bin"
}

$notices = Get-Content -Raw -Encoding UTF8 (Join-Path $templates 'THIRD-PARTY-NOTICES.header.txt')
if ($FfmpegDir) {
    # The "not shipped" bullet of the template no longer holds: FFmpeg is listed with the components above.
    $notices = [regex]::Replace($notices, '(?m)^- FFmpeg / FFprobe .*\r?\n(?:  .*\r?\n)*', '')
}
$notices = $notices.Replace('{{VERSION}}', $informational).Replace('{{COMPONENTS}}', ($lines -join "`r`n"))
Set-Content -Path (Join-Path $out 'THIRD-PARTY-NOTICES.txt') -Value $notices -Encoding UTF8

# --- The user's instruction and the model's document --------------------------------------------------------------
$readme = Get-Content -Raw -Encoding UTF8 (Join-Path $templates 'README.txt')
if ($FfmpegDir) {
    $bundled = (Get-Content -Raw -Encoding UTF8 (Join-Path $templates 'README.ffmpeg-bundled.txt')).TrimEnd() -replace '\r?\n', "`r`n"
    $readme = [regex]::Replace($readme, '(?m)^- FFmpeg \(ffmpeg\.exe.*$', { param($m) $bundled })
}
Set-Content -Path (Join-Path $out 'README.txt') -Value $readme -Encoding UTF8 -NoNewline
New-Item -ItemType Directory -Force (Join-Path $out 'docs') | Out-Null
Copy-Item (Join-Path $repo 'docs\AI_MODEL.md') (Join-Path $out 'docs\AI_MODEL.md')

# --- Build info and size ----------------------------------------------------------------------------------------
$runtime = ($components | Where-Object { $_.Id -eq "Microsoft.NETCore.App.Runtime.$rid" } | Select-Object -First 1)
$files = Get-ChildItem -Recurse -File $out
$bytes = ($files | Measure-Object -Sum Length).Sum
$info = @(
    "AI Video Editor $informational ($rid, self-contained, portable folder)",
    "Commit: $commit$(if ($dirty) { ' (uncommitted changes present)' })",
    "Built: $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')) UTC",
    ".NET SDK: $sdk",
    ".NET runtime: $(if ($runtime) { $runtime.Version } else { '(unknown)' })",
    "Files: $($files.Count + 1); size before this file: $bytes bytes ($([math]::Round($bytes / 1MB, 1)) MiB)",
    $ffmpegLine,
    'AI model: not included (docs\AI_MODEL.md).'
)
Set-Content -Path (Join-Path $out 'BUILD-INFO.txt') -Value $info -Encoding UTF8

$bytes = (Get-ChildItem -Recurse -File $out | Measure-Object -Sum Length).Sum
Write-Host "Portable folder: $out"
Write-Host "Size: $bytes bytes ($([math]::Round($bytes / 1MB, 1)) MiB), $((Get-ChildItem -Recurse -File $out).Count) files"

if ($Zip) {
    $zipPath = "$out.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $out -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Zip: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MiB)"
}
