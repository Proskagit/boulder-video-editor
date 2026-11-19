<#
.SYNOPSIS
    Checks the installer's install / update / uninstall paths on this machine (DECISIONS.md D070 §6) without touching the real
    installation or the real models: the installer is compiled again under a TEST identity — its own AppId, the name
    «Boulder Video Editor InstallerTest» and the per-user folder %LOCALAPPDATA%\BoulderVideoEditor-InstallerTest — from the
    stage of tools/distribution/New-Release.ps1.

.DESCRIPTION
    pwsh -File tools/installer/Test-Installer.ps1 -Stage artifacts/release/1.3.0/stage -Version 1.3.0 [-SkipAppStart]

    Scenarios (silent runs, each checked; a summary table at the end, exit code 1 on any failure):
      T1  install version V into the default per-user folder with the desktop shortcut: files, the .NET runtime and FFmpeg in
          place, the Start menu and desktop shortcuts, the uninstall entry (HKCU), every model file with its SHA-256; the
          installed application starts (unless -SkipAppStart: it reads the real per-user folder, writes only its log).
      T2  install V+1 over it: the same folder, one uninstall entry, the version updated, the desktop shortcut kept, the
          settings / recovery / cache / projects / other files of the models folder kept, a damaged model file repaired and
          the correct ones not copied again.
      T3  install V over V+1: refused by a silent run (the downgrade question answers No).
      T4  install while the application runs (its mutex held): refused, nothing changed.
      T5  uninstall: the program, its shortcuts and its entry removed; the models, the settings and the projects kept.
      T6  install again, uninstall with /DELETEMODELS=1: the model folders removed (also people-v1), the models folder kept
          because another file is in it, the settings and projects kept.
      W0–W3 the web Setup (D070 §7, unless -SkipWeb) against a local HTTP server on 127.0.0.1 (Python's http.server) standing
          for the models release: light; a tampered model on the server refused and never in place; the right files
          downloaded with their SHA-256; run again — nothing requested from the server; W4 an update with the server down and
          a model missing refused before any file is touched (the installed version kept).
    The test identity's folders are removed at the end (also after a failure); nothing of the real Boulder Video Editor is
    read or written by the installers.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Stage,
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Iscc,
    [switch] $SkipAppStart,
    [switch] $SkipWeb
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Stage = (Resolve-Path $Stage).Path
$appDir = Join-Path $Stage 'setup-app'
$modelsDir = Join-Path $Stage "BoulderVideoEditor-$Version-models"
foreach ($required in $appDir, $modelsDir) { if (-not (Test-Path $required)) { throw "Not in the stage: $required (run New-Release.ps1 first)" } }

$name = 'Boulder Video Editor InstallerTest'
$guid = '6E1F6A52-1B7B-4C1D-9F0B-0C3E2B5A7D11'
$dataFolderName = 'BoulderVideoEditor-InstallerTest'
$data = Join-Path $env:LOCALAPPDATA $dataFolderName
$models = Join-Path $data 'models'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{$guid}_is1"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"
$work = Join-Path ([IO.Path]::GetTempPath()) 'bve-installer-test'
$projects = Join-Path $work 'My projects'
$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $modelsDir 'models.json') | ConvertFrom-Json

$parts = $Version.Split('.')
$next = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"

$results = [System.Collections.Generic.List[object]]::new()
function Check([string] $scenario, [string] $what, [bool] $ok) {
    $results.Add([pscustomobject]@{ Scenario = $scenario; Check = $what; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }) })
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $what) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}

function Find-Iscc {
    if ($Iscc) { return $Iscc }
    foreach ($c in @($env:ISCC, (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'))) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    throw 'ISCC.exe not found'
}

function Build-TestSetup([string] $version) {
    $out = Join-Path $work "setup-$version"
    New-Item -ItemType Directory -Force $out | Out-Null
    & (Find-Iscc) '/Qp' "/DAppVersion=$version" "/DAppDir=$appDir" "/DModelsDir=$modelsDir" "/DModelsInclude=$include" "/DOutputDir=$out" `
        "/DRepoRoot=$repo" "/DAppName=$name" "/DAppIdGuid=$guid" "/DDataFolder=$dataFolderName" (Join-Path $PSScriptRoot 'BoulderVideoEditor.iss') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $version" }
    return Join-Path $out "BoulderVideoEditor-$version-setup.exe"
}

function Run-Setup([string] $setup, [string] $log, [string[]] $extra = @()) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $extra
    $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
    return $process.ExitCode
}

function Run-Uninstall([string[]] $extra = @()) {
    $entry = Get-ItemProperty $uninstallKey
    $exe = $entry.UninstallString.Trim('"')
    $process = Start-Process -FilePath $exe -ArgumentList (@('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $extra) -Wait -PassThru
    # The uninstaller runs a copy of itself from the temporary folder: wait until the entry is gone.
    for ($i = 0; $i -lt 240 -and (Test-Path $uninstallKey); $i++) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    return $process.ExitCode
}

function Test-Models {
    foreach ($folder in $manifest.folders) {
        foreach ($file in $folder.files) {
            $path = Join-Path $models "$($folder.folder)\$($file.name)"
            if (-not (Test-Path -LiteralPath $path)) { return $false }
            if ((Get-Item -LiteralPath $path).Length -ne $file.length) { return $false }
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.sha256) { return $false }
        }
    }
    return $true
}

function Entries { @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Where-Object { (Get-ItemProperty $_.PSPath).PSObject.Properties['DisplayName'] -and (Get-ItemProperty $_.PSPath).DisplayName -eq $name }) }

function Remove-TestIdentity {
    if (Test-Path $uninstallKey) { try { Run-Uninstall @('/DELETEMODELS=1') | Out-Null } catch { Write-Warning $_ } }
    foreach ($path in $data, $startMenu, $desktop) { if (Test-Path $path) { Remove-Item -Recurse -Force $path } }
}

$sentinels = @{
    (Join-Path $data 'config\settings.json')         = '{"sentinel":"settings"}'
    (Join-Path $data 'config\recent-projects.json')  = '{"sentinel":"recent"}'
    (Join-Path $data 'recovery\abc.json')            = '{"sentinel":"recovery"}'
    (Join-Path $data 'cache\people\x.bin')           = 'cache'
    (Join-Path $models 'notes.txt')                  = 'a file of the user in the models folder'
    (Join-Path $models 'people-v1\rfdetr_old.onnx')  = 'an older model'
    (Join-Path $projects 'Climb 1\project.json')     = '{"sentinel":"project"}'
    (Join-Path $projects 'Climb 1\export.mp4')       = 'an export'
}
function Test-Sentinels([string[]] $except = @()) {
    foreach ($entry in $sentinels.GetEnumerator()) {
        if ($except -contains $entry.Key) { continue }
        if (-not (Test-Path -LiteralPath $entry.Key)) { return $false }
        if ((Get-Content -Raw -LiteralPath $entry.Key) -ne $entry.Value) { return $false }
    }
    return $true
}

try {
    Remove-TestIdentity
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force $work | Out-Null
    $include = Join-Path $work 'models.generated.iss'
    & (Join-Path $PSScriptRoot 'New-ModelsInclude.ps1') -ModelsDir $modelsDir -OutFile $include
    Write-Host "Building the test installers $Version and $next…"
    $setupV = Build-TestSetup $Version
    $setupNext = Build-TestSetup $next

    # --- T1 ---------------------------------------------------------------------------------------------------------
    Write-Host "`nT1  install $Version"
    $code = Run-Setup $setupV (Join-Path $work 't1.log') @('/TASKS=desktopicon')
    Check 'T1' "setup exit code 0 (was $code)" ($code -eq 0)
    $entry = Get-ItemProperty $uninstallKey -ErrorAction SilentlyContinue
    Check 'T1' 'the uninstall entry in HKCU' ($null -ne $entry)
    $installDir = if ($entry) { $entry.InstallLocation.TrimEnd('\') } else { '' }
    Check 'T1' "the default folder is per-user ($installDir)" ($installDir -like (Join-Path $env:LOCALAPPDATA 'Programs\*'))
    Check 'T1' "the entry's version $Version" ($entry -and $entry.DisplayVersion -eq $Version)
    Check 'T1' 'BoulderVideoEditor.exe, the .NET runtime (coreclr.dll) and the README' (
        (Test-Path (Join-Path $installDir 'BoulderVideoEditor.exe')) -and (Test-Path (Join-Path $installDir 'coreclr.dll')) -and (Test-Path (Join-Path $installDir 'README.txt')))
    Check 'T1' 'no ai-models package in the program folder' (-not (Test-Path (Join-Path $installDir 'ai-models')))
    Check 'T1' 'the models notices in models-licenses\' (Test-Path (Join-Path $installDir 'models-licenses\THIRD-PARTY-NOTICES.txt'))
    $ffmpeg = Join-Path $installDir 'ffmpeg\bin\ffmpeg.exe'
    $ffmpegRuns = (Test-Path $ffmpeg) -and ((& $ffmpeg -hide_banner -version | Select-Object -First 1) -match 'aive-minimal')
    Check 'T1' 'FFmpeg in ffmpeg\bin runs' $ffmpegRuns
    $settings = Get-Content -Raw (Join-Path $installDir 'appsettings.json') | ConvertFrom-Json
    Check 'T1' 'appsettings.json points at ffmpeg\bin' ($settings.Ffmpeg.FfmpegPath -eq 'ffmpeg\bin\ffmpeg.exe' -and $settings.Ffmpeg.FfprobePath -eq 'ffmpeg\bin\ffprobe.exe')
    Check 'T1' 'the Start menu shortcut' (Test-Path $startMenu)
    Check 'T1' 'the desktop shortcut' (Test-Path $desktop)
    Check 'T1' 'every model file with its SHA-256' (Test-Models)

    if (-not $SkipAppStart) {
        # The log is open while the application runs (its write time is not updated): count the line instead, read shared.
        $log = Join-Path $env:LOCALAPPDATA "BoulderVideoEditor\logs\app-$((Get-Date).ToString('yyyyMMdd')).log"
        function Count-Initialized {
            if (-not (Test-Path $log)) { return 0 }
            $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try { $text = [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
            return ([regex]::Matches($text, 'Application shell initialized')).Count
        }
        $before = Count-Initialized
        $app = Start-Process -FilePath (Join-Path $installDir 'BoulderVideoEditor.exe') -WorkingDirectory $installDir -PassThru
        $initialized = $false
        for ($i = 0; $i -lt 60 -and -not $initialized -and -not $app.HasExited; $i++) {
            Start-Sleep -Milliseconds 500
            $initialized = (Count-Initialized) -gt $before
        }
        Check 'T1' 'the installed application starts (its log: the shell initialized)' ($initialized -and -not $app.HasExited)
        # T4 needs it running: its mutex is the one Inno Setup's AppMutex looks for.
        Write-Host "`nT4  install while the application runs"
        $code = Run-Setup $setupNext (Join-Path $work 't4.log')
        Check 'T4' "refused (exit code $code)" ($code -ne 0)
        Check 'T4' "the version is still $Version" ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $Version)
        if (-not $app.HasExited) { [void]$app.CloseMainWindow(); if (-not $app.WaitForExit(15000)) { $app.Kill() } }
    } else {
        Write-Host "`nT4  install while the application runs (its mutex held by this script)"
        $mutex = [System.Threading.Mutex]::new($false, 'BoulderVideoEditor.Running')
        try {
            $code = Run-Setup $setupNext (Join-Path $work 't4.log')
            Check 'T4' "refused (exit code $code)" ($code -ne 0)
            Check 'T4' "the version is still $Version" ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $Version)
        } finally { $mutex.Dispose() }
    }

    foreach ($entry in $sentinels.GetEnumerator()) {
        New-Item -ItemType Directory -Force (Split-Path $entry.Key) | Out-Null
        Set-Content -LiteralPath $entry.Key -Value $entry.Value -NoNewline
    }
    $damaged = Join-Path $models 'audio-v1\silero_vad.onnx'
    $bytes = [IO.File]::ReadAllBytes($damaged); $bytes[100] = $bytes[100] -bxor 0xFF; [IO.File]::WriteAllBytes($damaged, $bytes)

    # --- T2 ---------------------------------------------------------------------------------------------------------
    Write-Host "`nT2  update to $next"
    $code = Run-Setup $setupNext (Join-Path $work 't2.log')
    Check 'T2' "setup exit code 0 (was $code)" ($code -eq 0)
    $entry = Get-ItemProperty $uninstallKey
    Check 'T2' 'the same folder' ($entry.InstallLocation.TrimEnd('\') -eq $installDir)
    Check 'T2' "the version $next" ($entry.DisplayVersion -eq $next)
    Check 'T2' 'one uninstall entry (no second copy)' (@(Entries).Count -eq 1)
    Check 'T2' 'the desktop shortcut kept (the previous choice)' (Test-Path $desktop)
    Check 'T2' 'the damaged model repaired, every model with its SHA-256' (Test-Models)
    $log = Get-Content -Raw (Join-Path $work 't2.log')
    $copied = ([regex]::Matches($log, 'Model file to install: ')).Count
    $kept = ([regex]::Matches($log, 'Model file already in place: ')).Count
    $total = @($manifest.folders | ForEach-Object { $_.files }).Count
    Check 'T2' "only the damaged model copied ($copied copied, $kept kept of $total)" ($copied -eq 1 -and $kept -eq $total - 1)
    Check 'T2' 'settings, recent projects, recovery, cache, projects and other files kept' (Test-Sentinels)

    # --- T3 ---------------------------------------------------------------------------------------------------------
    Write-Host "`nT3  install $Version over $next"
    $code = Run-Setup $setupV (Join-Path $work 't3.log')
    Check 'T3' "refused by the silent run (exit code $code)" ($code -ne 0)
    Check 'T3' "the version is still $next" ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $next)

    # --- T5 ---------------------------------------------------------------------------------------------------------
    Write-Host "`nT5  uninstall, keeping the models"
    $code = Run-Uninstall
    Check 'T5' "uninstaller exit code 0 (was $code)" ($code -eq 0)
    Check 'T5' 'the uninstall entry removed' (-not (Test-Path $uninstallKey))
    Check 'T5' 'the program folder removed' (-not (Test-Path (Join-Path $installDir 'BoulderVideoEditor.exe')))
    Check 'T5' 'the shortcuts removed' (-not (Test-Path $startMenu) -and -not (Test-Path $desktop))
    Check 'T5' 'every model kept' (Test-Models)
    Check 'T5' 'settings, recovery, cache, projects and other files kept' (Test-Sentinels)

    # --- T6 ---------------------------------------------------------------------------------------------------------
    Write-Host "`nT6  install again, uninstall with the models"
    $code = Run-Setup $setupNext (Join-Path $work 't6.log')
    Check 'T6' "setup exit code 0 (was $code)" ($code -eq 0)
    $code = Run-Uninstall @('/DELETEMODELS=1')
    Check 'T6' "uninstaller exit code 0 (was $code)" ($code -eq 0)
    $folders = @($manifest.folders | ForEach-Object { $_.folder }) + 'people-v1'
    Check 'T6' "the model folders removed ($($folders -join ', '))" (@($folders | Where-Object { Test-Path (Join-Path $models $_) }).Count -eq 0)
    Check 'T6' 'the models folder kept for the file left in it' (Test-Path (Join-Path $models 'notes.txt'))
    Check 'T6' 'settings, recovery, cache and projects kept' (Test-Sentinels @((Join-Path $models 'people-v1\rfdetr_old.onnx')))

    # --- W1–W3: the web Setup against a local HTTP server (127.0.0.1) standing for the models release ----------------
    if (-not $SkipWeb) {
        $web = Join-Path $work 'web'
        New-Item -ItemType Directory -Force $web | Out-Null
        $damagedName = 'silero_vad.onnx'
        foreach ($folder in $manifest.folders) {
            foreach ($file in $folder.files) {
                $source = Join-Path $modelsDir "models\$($folder.folder)\$($file.name)"
                if ($file.name -eq $damagedName) {
                    $bytes = [IO.File]::ReadAllBytes($source); $bytes[100] = $bytes[100] -bxor 0xFF
                    [IO.File]::WriteAllBytes((Join-Path $web $file.name), $bytes)       # a tampered asset of the right size
                } else {
                    New-Item -ItemType HardLink -Path (Join-Path $web $file.name) -Target $source | Out-Null
                }
            }
        }
        $port = 18765
        $webInclude = Join-Path $work 'models.web.generated.iss'
        & (Join-Path $PSScriptRoot 'New-ModelsInclude.ps1') -ModelsDir $modelsDir -OutFile $webInclude -BaseUrl "http://127.0.0.1:$port/"
        $out = Join-Path $work 'setup-web'
        New-Item -ItemType Directory -Force $out | Out-Null
        & (Find-Iscc) '/Qp' "/DAppVersion=$next" "/DAppDir=$appDir" "/DModelsDir=$modelsDir" "/DModelsInclude=$webInclude" '/DWebModels=1' `
            "/DOutputDir=$out" "/DRepoRoot=$repo" "/DAppName=$name" "/DAppIdGuid=$guid" "/DDataFolder=$dataFolderName" (Join-Path $PSScriptRoot 'BoulderVideoEditor.iss') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'ISCC failed for the web Setup' }
        $webSetup = Join-Path $out "BoulderVideoEditor-$next-web-setup.exe"
        Check 'W0' ("the web Setup is light ({0:N0} MiB)" -f ((Get-Item $webSetup).Length / 1MB)) ((Get-Item $webSetup).Length -lt 300MB)

        $server = Start-Process -FilePath 'python' -ArgumentList @('-m', 'http.server', "$port", '--bind', '127.0.0.1', '--directory', "`"$web`"") `
            -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $work 'http.log') -RedirectStandardOutput (Join-Path $work 'http.out')
        try {
            Start-Sleep -Seconds 2
            Write-Host "`nW1  web Setup with a tampered model on the server"
            $code = Run-Setup $webSetup (Join-Path $work 'w1.log')
            Check 'W1' "refused (exit code $code)" ($code -ne 0)
            $bad = @(foreach ($folder in $manifest.folders) { foreach ($file in $folder.files) {
                $path = Join-Path $models "$($folder.folder)\$($file.name)"
                if ((Test-Path -LiteralPath $path) -and (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.sha256) { $path }
            } })
            Check 'W1' 'no model file without its SHA-256 took a place' ($bad.Count -eq 0)

            Write-Host "`nW2  web Setup with the right files"
            Remove-Item (Join-Path $web $damagedName)
            New-Item -ItemType HardLink -Path (Join-Path $web $damagedName) -Target (Join-Path $modelsDir "models\audio-v1\$damagedName") | Out-Null
            $code = Run-Setup $webSetup (Join-Path $work 'w2.log') @('/TASKS=desktopicon')
            Check 'W2' "setup exit code 0 (was $code)" ($code -eq 0)
            Check 'W2' 'every model downloaded with its SHA-256' (Test-Models)
            Check 'W2' 'the program installed (BoulderVideoEditor.exe)' ((Test-Path $uninstallKey) -and (Test-Path (Join-Path (Get-ItemProperty $uninstallKey).InstallLocation 'BoulderVideoEditor.exe')))
            Check 'W2' 'settings and projects kept' (Test-Sentinels @((Join-Path $models 'people-v1\rfdetr_old.onnx')))

            Write-Host "`nW3  web Setup again: nothing downloaded"
            $before = (Get-Content -Raw (Join-Path $work 'http.log') | Select-String -AllMatches 'GET /').Matches.Count
            $code = Run-Setup $webSetup (Join-Path $work 'w3.log')
            Check 'W3' "setup exit code 0 (was $code)" ($code -eq 0)
            $after = (Get-Content -Raw (Join-Path $work 'http.log') | Select-String -AllMatches 'GET /').Matches.Count
            Check 'W3' "no request to the server ($($after - $before))" ($after -eq $before)
            Check 'W3' 'every model in place' (Test-Models)
        } finally {
            if (-not $server.HasExited) { $server.Kill() }
        }

        Write-Host "`nW4  web update $next -> $third with the server down and a model missing"
        $third = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 2)"
        & (Find-Iscc) '/Qp' "/DAppVersion=$third" "/DAppDir=$appDir" "/DModelsDir=$modelsDir" "/DModelsInclude=$webInclude" '/DWebModels=1' `
            "/DOutputDir=$out" "/DRepoRoot=$repo" "/DAppName=$name" "/DAppIdGuid=$guid" "/DDataFolder=$dataFolderName" (Join-Path $PSScriptRoot 'BoulderVideoEditor.iss') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'ISCC failed for the second web Setup' }
        Remove-Item (Join-Path $models "audio-v1\$damagedName")
        $code = Run-Setup (Join-Path $out "BoulderVideoEditor-$third-web-setup.exe") (Join-Path $work 'w4.log')
        Check 'W4' "refused (exit code $code)" ($code -ne 0)
        Check 'W4' "the installed version is still $next" ((Get-ItemProperty $uninstallKey).DisplayVersion -eq $next)
        Check 'W4' 'no program file touched (no file entry in the setup log)' (-not (Select-String -Path (Join-Path $work 'w4.log') -SimpleMatch '-- File entry --' -Quiet))
    }
}
finally {
    Write-Host "`nCleaning up the test identity…"
    Remove-TestIdentity
    if (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$failed = @($results | Where-Object Result -eq 'FAIL').Count
Write-Host "$($results.Count - $failed) passed, $failed failed"
if ($failed -gt 0) { exit 1 }
