<#
.SYNOPSIS
  CI gate over the TRX results of `dotnet test` (D024 Step 9.9).

.DESCRIPTION
  Fails when
  - fewer TRX files than test projects were written (a project did not run),
  - any test did not pass and was not skipped (failed, error, timeout, aborted, ...),
  - any test was skipped other than the two known 4K scenes of ExportEndToEnd with the reason "Heavy scenario"
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
    'AiVideoEditor.ExportEndToEnd.Tests.ExportParityScaledTests.Source_4K_scaled_rotated_and_cropped'
)
$allowedReasonPrefix = 'Heavy scenario:'

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
                } elseif (-not $reason.StartsWith($allowedReasonPrefix)) {
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
