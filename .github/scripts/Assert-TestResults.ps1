<#
.SYNOPSIS
  CI gate over the TRX results of `dotnet test` (D024 Step 9.9).

.DESCRIPTION
  Fails when
  - fewer TRX files than test projects were written (a project did not run),
  - any test did not pass and was not skipped (failed, error, timeout, aborted, ...),
  - any test was skipped other than the known heavy scenes of ExportEndToEnd (the two 4K scenes and the long parallel
    export of D059) and the local scrubbing benchmark of Video.Tests (Phase 38, it needs a real file) with the reason
    "Heavy scenario", and the real NVENC encoding of D059 with the reason "NVENC unavailable" (the CI runner has no
    NVIDIA GPU; its trial encoding fails cleanly — the fallback the app relies on)
    (a skip because ffmpeg / ffprobe was not found is always an error).
  Prints passed / skipped / failed and every skip with its reason.
#>
param(
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [int] $ExpectedTrxFiles = 8
)

$ErrorActionPreference = 'Stop'

$allowedSkips = @(
    'AiVideoEditor.ExportEndToEnd.Tests.ExportParityScaledTests.Source_4K_full_canvas',
    'AiVideoEditor.ExportEndToEnd.Tests.ExportParityScaledTests.Source_4K_scaled_rotated_and_cropped',
    'AiVideoEditor.Video.Tests.ScrubBenchmarkTests.Scrub_a_real_file',
    'AiVideoEditor.ExportEndToEnd.Tests.ExportPipelineParityEndToEndTests.A_long_parallel_export_stays_equal_and_its_memory_stays_bounded',
    'AiVideoEditor.Video.Tests.NvencExportEncoderTests.Nvenc_encodes_a_valid_mp4_with_every_frame_in_order_and_the_D023_tags'
)
$allowedReasonPrefix = 'Heavy scenario:'
# D059: the NVENC test is skipped on a machine without NVENC (the CI runner), with its own reason.
$allowedReasonPrefixes = @{
    'AiVideoEditor.Video.Tests.NvencExportEncoderTests.Nvenc_encodes_a_valid_mp4_with_every_frame_in_order_and_the_D023_tags' = 'NVENC unavailable:'
}

$files = @(Get-ChildItem -Path $ResultsDirectory -Filter *.trx -Recurse -File)
$problems = New-Object System.Collections.Generic.List[string]
if ($files.Count -lt $ExpectedTrxFiles) {
    $problems.Add("Expected $ExpectedTrxFiles TRX files (one per test project), found $($files.Count).")
}

$passed = 0; $skipped = 0; $failed = 0
$seenAllowed = @{}
foreach ($file in $files) {
    [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
    $ns = New-Object System.Xml.XmlNamespaceManager($trx.NameTable)
    $ns.AddNamespace('t', $trx.DocumentElement.NamespaceURI)
    foreach ($result in $trx.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        $name = $result.GetAttribute('testName')
        $outcome = $result.GetAttribute('outcome')
        switch ($outcome) {
            'Passed' { $passed++ }
            'NotExecuted' {
                $skipped++
                $message = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
                $reason = if ($message) { $message.InnerText.Trim() } else { '' }
                Write-Host "Skipped: $name — $reason"
                if ($allowedSkips -notcontains $name) {
                    $problems.Add("Unexpected skip: $name ($reason)")
                } elseif (-not $reason.StartsWith($(if ($allowedReasonPrefixes.ContainsKey($name)) { $allowedReasonPrefixes[$name] } else { $allowedReasonPrefix }))) {
                    $problems.Add("Skip with an unexpected reason: $name ($reason)")
                } elseif ($seenAllowed.ContainsKey($name)) {
                    $problems.Add("Skipped more than once: $name")
                } else {
                    $seenAllowed[$name] = $true
                }
            }
            default {
                $failed++
                $problems.Add("Not passed ($outcome): $name")
            }
        }
    }
}

Write-Host "TRX files: $($files.Count); passed: $passed; skipped: $skipped; failed: $failed"
if ($env:GITHUB_STEP_SUMMARY) {
    "### Tests`n`nTRX files: $($files.Count) · passed: **$passed** · skipped: **$skipped** · failed: **$failed**" |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "::error::$_" }
    exit 1
}
