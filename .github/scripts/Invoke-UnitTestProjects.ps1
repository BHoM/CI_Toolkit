# Invoke-UnitTestProjects.ps1 — the ci-unit-tests run: build, list, run and judge each test
# project, then write the per-project table and the annotations.
#
# Dot-sourced by .github/actions/ci-unit-tests/action.yml and by
# .github/scripts/tests/Invoke-UnitTestProjects.Tests.ps1. Defines functions and sets the
# strictness below, nothing else, so dot-sourcing runs nothing.
#
# The decisions live in pure functions (Read-TestListing, Get-ExcludedListingSources,
# Read-TrxResult, Get-ProjectVerdict, Get-RunReport) so they can be tested against recorded
# outputs. Invoke-UnitTestProjects is the only function that runs dotnet or writes anything.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-TestListing {
    <#
    .SYNOPSIS
      Reads `dotnet test --list-tests` output into a count, or reports it unreadable.

    .DESCRIPTION
      Three outcomes to separate, and the shape of the output is the only evidence available:

        a real listing   marker, then one indented line per test
        no test at all   marker, then an unindented "No test is available"
        a broken host    marker, then an exception at column 0 and a trace

      The marker is printed in all three, so it cannot decide anything on its own. The listing
      is the contiguous indented block after it, and the diagnostics are judged only on the
      lines outside that block: a test display name may legitimately contain the word Exception
      (`LoadAssembliesShouldThrowException`, or a parameterised case rendered as
      `Throws(System.InvalidOperationException)`), so matching it anywhere in the output would
      read a healthy project as broken.

    .OUTPUTS
      [pscustomobject] with Read (bool) and Discovered (int, meaningful only when Read).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Output,
        [Parameter(Mandatory)][int]$ExitCode
    )

    $lines  = $Output -split "`r?`n"
    $marker = [Array]::FindIndex([string[]]$lines, [Predicate[string]]{ param($l) $l -match 'The following Tests are available' })

    $listing = @()
    if ($marker -ge 0) {
        for ($i = $marker + 1; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch '^\s+\S') { break }
            $listing += $lines[$i]
        }
    }
    $blockEnd = $marker + $listing.Count
    $outside  = @(for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($marker -lt 0 -or $i -le $marker -or $i -gt $blockEnd) { $lines[$i] }
    })

    # VSTest states a zero-test assembly explicitly, unindented.
    $zeroStated = @($lines | Where-Object { $_ -match '^No test is available in ' }).Count -gt 0
    # Test-host failures surface as a TestPlatformException, a stack frame
    # (`   at Namespace.Type.Method(`) or the diagnostic-logs hint, all of them outside the
    # listing block.
    $brokenHost = @($outside | Where-Object {
        $_ -match 'TestPlatformException' -or
        $_ -match '^\s*at [\w.`+<>]+\.[\w`+<>]+\(' -or
        $_ -match 'Please check the diagnostic logs'
    }).Count -gt 0

    $discovered = $listing.Count
    $read = $ExitCode -eq 0 -and -not $brokenHost -and ($discovered -gt 0 -or $zeroStated)
    [pscustomobject]@{ Read = $read; Discovered = $discovered }
}

function Get-ExcludedListingSources {
    <#
    .SYNOPSIS
      The test assemblies named in `dotnet test --list-tests` output, one per target framework.

    .DESCRIPTION
      The excluded tests never reach the results files, so vstest lists them by category from
      these assemblies. Each one is written to the same file, so a multi-targeted project keeps
      only the last framework's listing.

    .OUTPUTS
      [string[]] assembly paths, in the order printed.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Output)

    @([regex]::Matches($Output, '(?m)^Test run for (.+?\.dll) \(') | ForEach-Object { $_.Groups[1].Value })
}

function Read-TrxResult {
    <#
    .SYNOPSIS
      Reads a .trx file's counters and the names of the tests it did not execute.

    .DESCRIPTION
      A results file that parses but carries no counters proves nothing. Reading its absent
      attributes as zero would make it indistinguishable from "every test carried the
      category", which is a pass, so it is returned as $null like a file that does not parse.

    .OUTPUTS
      $null, or [pscustomobject] with Total, Executed, Failed and NotRunNames.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    try {
        [xml]$trx = Get-Content -Path $Path -Raw
    } catch {
        Write-Host "::debug::TRX $Path did not parse: $($_.Exception.Message)"
        return $null
    }
    if (-not $trx.DocumentElement) { return $null }

    $counters = $trx.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if (-not $counters -or -not $counters.HasAttribute('total')) { return $null }

    # Tests the filter admitted and the run did not execute: inconclusive, ignored, or anything
    # else the adapter declines to run. The dedicated `inconclusive` and `notExecuted` counters
    # stay at 0 for NUnit, so the outcomes cannot be told apart and do not need to be.
    $notRunNames = @($trx.SelectNodes("/*[local-name()='TestRun']/*[local-name()='Results']/*[local-name()='UnitTestResult']") |
        Where-Object { $_.GetAttribute('outcome') -ne 'Passed' -and $_.GetAttribute('outcome') -ne 'Failed' } |
        ForEach-Object { $_.GetAttribute('testName') })

    [pscustomobject]@{
        Total       = [int]$counters.GetAttribute('total')
        Executed    = [int]$counters.GetAttribute('executed')
        Failed      = [int]$counters.GetAttribute('failed')
        NotRunNames = $notRunNames
    }
}

function Get-ProjectVerdict {
    <#
    .SYNOPSIS
      Judges one test project from what each stage of its run produced.

    .DESCRIPTION
      Pass only the stages that were reached: a project that did not build has no listing, and
      a project whose listing could not be read was never run. Results is $null both when no
      .trx was written (TrxFound false) and when one was written but could not be read.

    .OUTPUTS
      [pscustomobject] with Name, Category, Row (the per-project table row), Annotations (the
      lines to print as soon as the project is judged), and the figures the report needs:
      Discovered, Executed, NotRun, NotRunNames, Total.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$BuildExitCode,
        $Listing = $null,
        [bool]$TrxFound = $false,
        $Results = $null,
        [int]$TestExitCode = 0
    )

    function Verdict($category, $row, [string[]]$annotations = @(), $figures = @{}) {
        $v = [ordered]@{
            Name = $Name; Category = $category; Row = $row; Annotations = $annotations
            Discovered = 0; Executed = 0; NotRun = 0; NotRunNames = @(); Total = 0
        }
        foreach ($k in $figures.Keys) { $v[$k] = $figures[$k] }
        [pscustomobject]$v
    }
    function Row($d, $ran, $ex, $nr, $f, $result) {
        [pscustomobject]@{ Project = $Name; Discovered = $d; Ran = $ran; Excluded = $ex; NotRun = $nr; Failed = $f; Result = $result }
    }

    if ($BuildExitCode -ne 0) {
        return Verdict 'BuildFailed' (Row '-' '-' '-' '-' '-' 'build failed')
    }
    if (-not $Listing -or -not $Listing.Read) {
        return Verdict 'Undetermined' (Row '?' '-' '-' '-' '-' 'count unreadable')
    }
    $discovered = $Listing.Discovered
    if ($discovered -eq 0) {
        return Verdict 'NoTests' (Row 0 0 0 0 0 'no tests')
    }
    if (-not $TrxFound) {
        return Verdict 'NoOutput' (Row $discovered '-' '-' '-' '-' 'no test output') -figures @{ Discovered = $discovered }
    }
    if (-not $Results) {
        return Verdict 'BadResults' (Row $discovered '-' '-' '-' '-' 'results unreadable') -figures @{ Discovered = $discovered }
    }

    $total    = $Results.Total
    $executed = $Results.Executed
    $failedN  = $Results.Failed
    $excluded = $discovered - $total
    $notRun   = $total - $executed
    $annotations = @()

    # A negative figure means the listing and the run disagree on how many cases a
    # parameterised test expands to, so neither the excluded count nor anything derived from it
    # can be trusted for this project.
    if ($excluded -lt 0) {
        $annotations += "::warning title=UnitTests::${Name}: the run matched $total tests but only $discovered were listed. The excluded count is not meaningful here, which usually means parameterised cases expand differently in the listing and the run."
    }

    if ($TestExitCode -ne 0 -or $failedN -gt 0) {
        $category = 'TestFailed'; $result = 'failed'
    } elseif ($total -eq 0) {
        # Every test carried the category. Nothing ran, and that is the intended outcome rather
        # than a defect.
        $category = 'AllExcluded'; $result = 'all excluded'
    } elseif ($executed -eq 0) {
        # Tests survived the filter and still none ran. A check that passes here reports green
        # on a project that examined nothing.
        $category = 'NoneExecuted'; $result = 'none executed'
    } else {
        $category = 'Passed'
        $result = if ($notRun -gt 0) { 'passed, some not run' } else { 'passed' }
    }

    if ($excluded -gt 0) {
        $annotations += "::notice title=UnitTests::${Name}: skipped: $excluded tests require external software"
    }
    if ($executed -gt 0 -and $notRun -gt 0) {
        # GitHub truncates an annotation at roughly four thousand characters, and a
        # parameterised fixture can produce hundreds of names. Cap the annotation and keep the
        # full list in the job summary, which has no such limit.
        $shown = @($Results.NotRunNames | Select-Object -First 20)
        $tail  = if ($Results.NotRunNames.Count -gt $shown.Count) { " and $($Results.NotRunNames.Count - $shown.Count) more" } else { "" }
        $annotations += "::warning title=UnitTests::${Name}: $notRun of $total tests did not run: $($shown -join ', ')$tail"
    }

    Verdict $category (Row $discovered $executed $excluded $notRun $failedN $result) $annotations @{
        Discovered = $discovered; Executed = $executed; NotRun = $notRun
        NotRunNames = $Results.NotRunNames; Total = $total
    }
}

function Get-RunReport {
    <#
    .SYNOPSIS
      Turns every project's verdict into the job summary, the closing annotations and the exit
      code.

    .OUTPUTS
      [pscustomobject] with SummaryLines, Annotations and ExitCode (0 or 1).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Verdicts,
        [Parameter(Mandatory)][string]$Category
    )

    $filter = "TestCategory!=$Category"
    $of = { param($c) @($Verdicts | Where-Object { $_.Category -eq $c }) }

    $summary = @(
        "### Unit tests"
        ""
        "Filter: ``$filter``"
        ""
        "| Project | Discovered | Ran | Excluded | Not run | Failed | Result |"
        "|---|---:|---:|---:|---:|---:|---|"
    )
    foreach ($v in $Verdicts) {
        $r = $v.Row
        $summary += "| $($r.Project) | $($r.Discovered) | $($r.Ran) | $($r.Excluded) | $($r.NotRun) | $($r.Failed) | $($r.Result) |"
    }

    $partialNotRun = @($Verdicts | Where-Object { $_.Executed -gt 0 -and $_.NotRun -gt 0 })
    if ($partialNotRun) {
        $names = @(foreach ($p in $partialNotRun) { foreach ($t in $p.NotRunNames) { "- ``$($p.Name)``: $t" } })
        $summary += @("", "<details><summary>Not run ($($names.Count))</summary>", "") + $names + @("", "</details>")
    }

    $annotations = @()
    $allExcluded = & $of 'AllExcluded'
    foreach ($v in $allExcluded) {
        $annotations += "::notice title=UnitTests::$($v.Name): every test carries [Category(`"$Category`")], so none ran. This is a pass."
    }

    # Only claim category exclusion when category exclusion is what happened. The earlier form
    # fired on `ranTotal -eq 0` alone and so asserted it about a repository whose tests were all
    # inconclusive instead.
    $notJudged   = @('BuildFailed', 'Undetermined', 'NoOutput', 'BadResults')
    $ranProjects = @($Verdicts | Where-Object { $notJudged -notcontains $_.Category })
    $ranTotal    = 0
    foreach ($v in $Verdicts) { $ranTotal += $v.Executed }
    if ($ranTotal -eq 0 -and $ranProjects.Count -gt 0 -and $allExcluded.Count -eq $ranProjects.Count) {
        $annotations += "::warning title=UnitTests::No test ran in this repository: every discovered test carries [Category(`"$Category`")]."
    }

    # A test project with no test is a defect in the project, not a pass. The previous guard
    # compared .trx file counts, which never caught this: `dotnet test` writes a results file
    # with total=0 and exits 0 whether the project is empty or the filter matched nothing.
    # Counters tell the two apart; file presence does not.
    $names = { param($c) (& $of $c | ForEach-Object { $_.Name }) -join ', ' }
    if (& $of 'BuildFailed') { $annotations += "::error title=UnitTests::Build failed for project(s): $(& $names 'BuildFailed')" }
    foreach ($v in (& $of 'Undetermined')) {
        $annotations += "::error title=UnitTests::could not determine test count for $($v.Name) . ``dotnet test --list-tests`` neither listed a test nor reported that none was available, so the project cannot be judged. This is a fault in the check or in the project's test host, not evidence about its tests."
    }
    if (& $of 'NoTests') { $annotations += "::error title=UnitTests::No test found in project(s): $(& $names 'NoTests') . The project compiled but declares no test. Remove the project or add tests." }
    foreach ($v in (& $of 'NoneExecuted')) {
        $annotations += "::error title=UnitTests::no tests executed in $($v.Name) ($($v.Discovered) discovered, $($v.NotRun) not run). The filter admitted these tests and none of them ran, so this project examined nothing. Tag them [Category(`"$Category`")] if they need software a runner cannot have, otherwise fix what is stopping them."
    }
    if (& $of 'NoOutput')   { $annotations += "::error title=UnitTests::No test output from project(s): $(& $names 'NoOutput') . The run produced no .trx, which indicates a runner load failure." }
    if (& $of 'BadResults') { $annotations += "::error title=UnitTests::Test results could not be read for project(s): $(& $names 'BadResults') . The .trx exists but carries no ResultSummary/Counters, so the run cannot be judged." }
    if (& $of 'TestFailed') { $annotations += "::error title=UnitTests::Test runs failed for project(s): $(& $names 'TestFailed')" }

    $failing = @('BuildFailed', 'Undetermined', 'NoTests', 'NoneExecuted', 'NoOutput', 'BadResults', 'TestFailed')
    $exit = if (@($Verdicts | Where-Object { $failing -contains $_.Category }).Count) { 1 } else { 0 }

    [pscustomobject]@{ SummaryLines = $summary; Annotations = $annotations; ExitCode = $exit }
}

function Invoke-UnitTestProjects {
    <#
    .SYNOPSIS
      Runs every test project and reports on it. The only function here with side effects.

    .DESCRIPTION
      Per-project invocation, not `dotnet test <sln>`. BHoM `_Tests.sln` files map every
      project's `Test|Any CPU` to `Debug|Any CPU`, so a solution-level build silently compiles
      Debug and the test host (which only has an executable TFM under Test) is never produced.
      Iterating `.csproj` files honours `--configuration Test` correctly.

      dotnet's own output is sent to the host, not returned, so the log reads as it did when
      this ran inline and the function returns only the exit code.

    .OUTPUTS
      [int] 0 when every project passed, 1 otherwise.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Projects,
        [Parameter(Mandatory)][string]$Category,
        [Parameter(Mandatory)][string]$SummaryPath,
        [string]$ResultsDirectory = 'TestResults'
    )

    $filter   = "TestCategory!=$Category"
    $verdicts = @()

    foreach ($proj in $Projects) {
        $name = [IO.Path]::GetFileNameWithoutExtension($proj)

        # Build separately so a compile failure is reported as one, rather than arriving as an
        # opaque non-zero exit from `dotnet test`.
        Write-Host "::group::dotnet build $name"
        dotnet build $proj -c Test --nologo | Out-Host
        $buildExit = $LASTEXITCODE
        Write-Host "::endgroup::"
        if ($buildExit -ne 0) {
            $verdicts += Get-ProjectVerdict -Name $name -BuildExitCode $buildExit
            continue
        }

        # Unfiltered discovery count. The run below is filtered, so its TRX cannot tell "no
        # tests here" from "all of them were excluded", and those two need opposite verdicts.
        # `--list-tests` answers it for one extra invocation against an already-built project.
        Write-Host "::group::dotnet test $name --list-tests"
        $listOut  = (dotnet test $proj -c Test --no-build --list-tests 2>&1 | Out-String)
        $listExit = $LASTEXITCODE
        Write-Host $listOut
        Write-Host "::endgroup::"

        $listing = Read-TestListing -Output $listOut -ExitCode $listExit
        if (-not $listing.Read -or $listing.Discovered -eq 0) {
            $verdicts += Get-ProjectVerdict -Name $name -BuildExitCode 0 -Listing $listing
            continue
        }

        New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
        foreach ($dll in (Get-ExcludedListingSources -Output $listOut)) {
            dotnet vstest $dll /ListFullyQualifiedTests `
                "/ListTestsTargetPath:$(Join-Path $PWD "$ResultsDirectory/$name.excluded.txt")" `
                "/TestCaseFilter:TestCategory=$Category" | Out-Null
        }

        Write-Host "::group::dotnet test $name --filter $filter"
        dotnet test $proj -c Test --no-build --filter $filter `
            --collect:"XPlat Code Coverage" `
            --logger "trx;LogFileName=$name.trx" --logger "html;LogFileName=$name.html" `
            --results-directory $ResultsDirectory | Out-Host
        $exit = $LASTEXITCODE
        Write-Host "::endgroup::"

        $trx      = Join-Path $ResultsDirectory "$name.trx"
        $trxFound = Test-Path $trx
        $results  = if ($trxFound) { Read-TrxResult -Path $trx } else { $null }
        $verdict  = Get-ProjectVerdict -Name $name -BuildExitCode 0 -Listing $listing `
            -TrxFound $trxFound -Results $results -TestExitCode $exit
        foreach ($a in $verdict.Annotations) { Write-Host $a }
        $verdicts += $verdict
    }

    # Per-project table, in the job summary and the log.
    $rows  = @($verdicts | ForEach-Object { $_.Row })
    Write-Host ($rows | Format-Table -AutoSize | Out-String)

    $report = Get-RunReport -Verdicts $verdicts -Category $Category
    $report.SummaryLines | Add-Content -Path $SummaryPath
    foreach ($a in $report.Annotations) { Write-Host $a }
    $report.ExitCode
}
