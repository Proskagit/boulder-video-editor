<#
.SYNOPSIS
    Writes a Windows Sandbox configuration for S1's clean-machine check (DECISIONS.md D066 §5).

.DESCRIPTION
    pwsh -File tools/portable/New-SandboxCheck.ps1 -Media <folder with COPIES of test videos / a FIT> [-Release <folder>]
    Writes artifacts/release/S1-clean-check.wsb. Opening it starts a fresh Windows (Windows Sandbox: no .NET, no FFmpeg,
    no models, no settings of this machine) with
      Desktop\release - the release folder (the portable zip and the models zip), read-only;
      Desktop\media   - the media folder, read-only (the originals are never touched; give copies anyway).
    The graphics card is switched off in the sandbox (<VGpu>Disable</VGpu>): DirectML and NVENC are unavailable there, so the
    check also covers the CPU fallbacks; the GPU paths are checked on this machine. The sandbox and everything in it are
    discarded when it is closed. Windows Sandbox must be enabled (Windows features: "Windows Sandbox").
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Media,
    [string] $Release
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Release) { $Release = Join-Path $repo 'artifacts\release' }
$Release = (Resolve-Path $Release).Path
$Media = (Resolve-Path $Media).Path

function Escape([string] $s) { [System.Security.SecurityElement]::Escape($s) }

$wsb = @"
<Configuration>
  <VGpu>Disable</VGpu>
  <Networking>Disable</Networking>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$(Escape $Release)</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\release</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$(Escape $Media)</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\media</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
</Configuration>
"@
$path = Join-Path $Release 'S1-clean-check.wsb'
Set-Content -Path $path -Value $wsb -Encoding UTF8
Write-Host "Windows Sandbox configuration: $path"
Write-Host 'Open it (double click). Inside: copy release\*.zip to the desktop, unpack, run Install-Models.cmd, then AiVideoEditor.exe.'
