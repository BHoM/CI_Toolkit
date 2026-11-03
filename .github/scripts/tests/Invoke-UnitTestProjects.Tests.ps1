# Invoke-UnitTestProjects.Tests.ps1: Pester tests for the ci-unit-tests run's decisions.
#
# The fixtures are real outputs recorded from purpose-built probe projects, one per project
# state: `dotnet test --list-tests` output as listing-<case>.txt, and the .trx reduced to the
# elements the script reads as results-<case>.trx. Paths in the listings were replaced with a
# neutral D:\a\Sample\Sample\ checkout. Three results files are synthetic because no project
# produces them on demand: one without counters, one that is not XML, and one that is empty.
#
# Only the pure functions are tested. Invoke-UnitTestProjects runs dotnet and is covered by
# running the action.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    . (Join-Path $repoRoot '.github/scripts/Invoke-UnitTestProjects.ps1')

    $script:fixtures = Join-Path $PSScriptRoot 'fixtures/Invoke-UnitTestProjects'

    function Get-Listing([string]$Case) {
        Get-Content (Join-Path $script:fixtures "listing-$Case.txt") -Raw
    }
    function Get-ResultsPath([string]$Case) {
        Join-Path $script:fixtures "results-$Case.trx"
    }

    # The verdict for a project that got as far as a results file, from its recorded outputs.
    function Get-RecordedVerdict([string]$Case, [int]$TestExitCode = 0) {
        $listing = Read-TestListing -Output (Get-Listing $Case) -ExitCode 0
        $results = Read-TrxResult -Path (Get-ResultsPath $Case)
        Get-ProjectVerdict -Name "$Case-project" -BuildExitCode 0 -Listing $listing `
            -TrxFound $true -Results $results -TestExitCode $TestExitCode
    }
}

Describe 'Read-TestListing' {

    It 'counts every indented line after the marker' {
        $l = Read-TestListing -Output (Get-Listing 'parameterised') -ExitCode 0
        $l.Read       | Should -BeTrue
        $l.Discovered | Should -Be 16
    }

    It 'counts duplicate display names once each, not once per distinct name' {
        # Two fixtures each declare Same, and two generic fixtures each declare GenericRuns and
        # GenericExcluded. The listing prints short names, so they repeat.
        $l = Read-TestListing -Output (Get-Listing 'duplicate-names') -ExitCode 0
        $l.Discovered | Should -Be 9
    }

    It 'reads an explicit "No test is available" as a readable zero' {
        $l = Read-TestListing -Output (Get-Listing 'no-tests') -ExitCode 0
        $l.Read       | Should -BeTrue
        $l.Discovered | Should -Be 0
    }

    It 'reports a broken test host as unreadable even though the exit code is 0' {
        $l = Read-TestListing -Output (Get-Listing 'unreadable-listing') -ExitCode 0
        $l.Read | Should -BeFalse
    }

    It 'reports a listing as unreadable when dotnet exits non-zero' {
        $l = Read-TestListing -Output (Get-Listing 'parameterised') -ExitCode 1
        $l.Read | Should -BeFalse
    }

    It 'reports output with no marker and no zero statement as unreadable' {
        $l = Read-TestListing -Output 'Build started...' -ExitCode 0
        $l.Read | Should -BeFalse
    }

    It 'does not read a test named after an exception as a broken host' {
        $out = "Test run for D:\a\Sample\x.dll (.NETCoreApp,Version=v8.0)`nThe following Tests are available:`n    LoadAssembliesShouldThrowException`n    Throws(System.InvalidOperationException)`n"
        $l = Read-TestListing -Output $out -ExitCode 0
        $l.Read       | Should -BeTrue
        $l.Discovered | Should -Be 2
    }
}

Describe 'Get-ExcludedListingSources' {

    It 'names the assembly from the "Test run for" line' {
        $s = @(Get-ExcludedListingSources -Output (Get-Listing 'duplicate-names'))
        $s.Count | Should -Be 1
        $s[0]    | Should -Be 'D:\a\Sample\Sample\.ci\unit-tests\ExcludedNames_Tests\bin\Test\net8.0\ExcludedNames_Tests.dll'
    }

    It 'names one assembly per target framework' {
        $out = "Test run for D:\a\x\net8.0\X.dll (.NETCoreApp,Version=v8.0)`nThe following Tests are available:`n    A`nTest run for D:\a\x\net48\X.dll (.NETFramework,Version=v4.8)`nThe following Tests are available:`n    A`n"
        @(Get-ExcludedListingSources -Output $out) | Should -Be @('D:\a\x\net8.0\X.dll', 'D:\a\x\net48\X.dll')
    }

    It 'returns nothing when no assembly is named' {
        @(Get-ExcludedListingSources -Output 'no assembly here').Count | Should -Be 0
    }
}

Describe 'Read-TrxResult' {

    It 'reads the counters' {
        $r = Read-TrxResult -Path (Get-ResultsPath 'failing')
        $r.Total    | Should -Be 2
        $r.Executed | Should -Be 2
        $r.Failed   | Should -Be 1
    }

    It 'names the tests that were neither passed nor failed' {
        $r = Read-TrxResult -Path (Get-ResultsPath 'some-not-run')
        $r.NotRunNames | Should -Be @('ThisOneIsInconclusive')
    }

    It 'returns $null for a results file without counters' {
        Read-TrxResult -Path (Get-ResultsPath 'no-counters') | Should -BeNullOrEmpty
    }

    It 'returns $null for a results file that is not XML' {
        Read-TrxResult -Path (Get-ResultsPath 'not-xml') | Should -BeNullOrEmpty
    }

    It 'returns $null for an empty results file rather than throwing' {
        Read-TrxResult -Path (Get-ResultsPath 'empty') | Should -BeNullOrEmpty
    }
}

Describe 'Get-ProjectVerdict' {

    Context 'projects stopped before a run' {

        It 'build failed' {
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 1
            $v.Category   | Should -Be 'BuildFailed'
            $v.Row.Result | Should -Be 'build failed'
            $v.Row.Discovered | Should -Be '-'
        }

        It 'unreadable listing' {
            $l = Read-TestListing -Output (Get-Listing 'unreadable-listing') -ExitCode 0
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 0 -Listing $l
            $v.Category       | Should -Be 'Undetermined'
            $v.Row.Discovered | Should -Be '?'
            $v.Row.Result     | Should -Be 'count unreadable'
        }

        It 'no tests' {
            $l = Read-TestListing -Output (Get-Listing 'no-tests') -ExitCode 0
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 0 -Listing $l
            $v.Category   | Should -Be 'NoTests'
            $v.Row.Result | Should -Be 'no tests'
        }

        It 'no results file' {
            $l = Read-TestListing -Output (Get-Listing 'parameterised') -ExitCode 0
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 0 -Listing $l -TrxFound $false
            $v.Category       | Should -Be 'NoOutput'
            $v.Row.Discovered | Should -Be 16
            $v.Row.Result     | Should -Be 'no test output'
        }

        It 'results file without counters' {
            $l = Read-TestListing -Output (Get-Listing 'parameterised') -ExitCode 0
            $r = Read-TrxResult -Path (Get-ResultsPath 'no-counters')
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 0 -Listing $l -TrxFound $true -Results $r
            $v.Category   | Should -Be 'BadResults'
            $v.Row.Result | Should -Be 'results unreadable'
        }
    }

    Context 'projects that ran' {

        It 'all excluded is a pass and is announced' {
            $v = Get-RecordedVerdict 'all-excluded'
            $v.Category     | Should -Be 'AllExcluded'
            $v.Row.Excluded | Should -Be 2
            $v.Row.Result   | Should -Be 'all excluded'
            $v.Annotations  | Should -Be @('::notice title=UnitTests::all-excluded-project: skipped: 2 tests require external software')
        }

        It 'none executed is its own verdict' {
            $v = Get-RecordedVerdict 'none-executed'
            $v.Category   | Should -Be 'NoneExecuted'
            $v.Row.Ran    | Should -Be 0
            $v.Row.NotRun | Should -Be 2
            $v.Annotations | Should -BeNullOrEmpty
        }

        It 'some not run passes and warns with the names' {
            $v = Get-RecordedVerdict 'some-not-run'
            $v.Category   | Should -Be 'Passed'
            $v.Row.Result | Should -Be 'passed, some not run'
            $v.Annotations | Should -Be @('::warning title=UnitTests::some-not-run-project: 1 of 2 tests did not run: ThisOneIsInconclusive')
        }

        It 'a failed test fails the project' {
            $v = Get-RecordedVerdict 'failing' -TestExitCode 1
            $v.Category   | Should -Be 'TestFailed'
            $v.Row.Failed | Should -Be 1
            $v.Row.Result | Should -Be 'failed'
        }

        It 'a non-zero test exit fails the project even with no failed test' {
            $v = Get-RecordedVerdict 'some-not-run' -TestExitCode 1
            $v.Category | Should -Be 'TestFailed'
        }

        It 'parameterised cases are counted per case' {
            $v = Get-RecordedVerdict 'parameterised'
            $v.Row.Discovered | Should -Be 16
            $v.Row.Ran        | Should -Be 13
            $v.Row.Excluded   | Should -Be 1
            $v.Row.NotRun     | Should -Be 2
            $v.Row.Result     | Should -Be 'passed, some not run'
        }

        It 'duplicate names do not change the counts' {
            $v = Get-RecordedVerdict 'duplicate-names'
            $v.Row.Discovered | Should -Be 9
            $v.Row.Ran        | Should -Be 4
            $v.Row.Excluded   | Should -Be 5
            $v.Row.Result     | Should -Be 'passed'
        }

        It 'warns when the run matched more tests than were listed' {
            $l = [pscustomobject]@{ Read = $true; Discovered = 1 }
            $r = [pscustomobject]@{ Total = 3; Executed = 3; Failed = 0; NotRunNames = @() }
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 0 -Listing $l -TrxFound $true -Results $r
            $v.Annotations[0] | Should -Match 'the run matched 3 tests but only 1 were listed'
        }

        It 'caps the not-run annotation at twenty names' {
            $l = [pscustomobject]@{ Read = $true; Discovered = 30 }
            $r = [pscustomobject]@{ Total = 30; Executed = 5; Failed = 0; NotRunNames = @(1..25 | ForEach-Object { "T$_" }) }
            $v = Get-ProjectVerdict -Name 'P' -BuildExitCode 0 -Listing $l -TrxFound $true -Results $r
            $v.Annotations[-1] | Should -Match 'T20 and 5 more$'
        }
    }
}

Describe 'Get-RunReport' {

    BeforeAll {
        $script:allExcluded = Get-RecordedVerdict 'all-excluded'
        $script:someNotRun  = Get-RecordedVerdict 'some-not-run'
        $script:failing     = Get-RecordedVerdict 'failing' -TestExitCode 1
        $script:noneRan     = Get-RecordedVerdict 'none-executed'
        $script:built       = Get-ProjectVerdict -Name 'Broken' -BuildExitCode 1

        $listed = [pscustomobject]@{ Read = $true; Discovered = 2 }
        $script:noTests = Get-ProjectVerdict -Name 'Empty' -BuildExitCode 0 `
            -Listing (Read-TestListing -Output (Get-Listing 'no-tests') -ExitCode 0)
        $script:unread  = Get-ProjectVerdict -Name 'Unread' -BuildExitCode 0 `
            -Listing (Read-TestListing -Output (Get-Listing 'unreadable-listing') -ExitCode 0)
        $script:noOut   = Get-ProjectVerdict -Name 'NoTrx' -BuildExitCode 0 -Listing $listed -TrxFound $false
        $script:badTrx  = Get-ProjectVerdict -Name 'BadTrx' -BuildExitCode 0 -Listing $listed -TrxFound $true `
            -Results (Read-TrxResult -Path (Get-ResultsPath 'no-counters'))

        $script:byCategory = @{
            TestFailed = $failing; NoneExecuted = $noneRan; BuildFailed = $built; NoTests = $noTests
            Undetermined = $unread; NoOutput = $noOut; BadResults = $badTrx
        }
    }

    It 'passes when every project passed or was all excluded' {
        (Get-RunReport -Verdicts @($allExcluded, $someNotRun) -Category 'RequiresExternalSoftware').ExitCode | Should -Be 0
    }

    # Every failing category, beside a passing project, so dropping any one of them from the
    # failing set fails this test.
    It 'fails when one project is <Category>' -ForEach @(
        @{ Category = 'TestFailed' }, @{ Category = 'NoneExecuted' }, @{ Category = 'BuildFailed' },
        @{ Category = 'NoTests' }, @{ Category = 'Undetermined' }, @{ Category = 'NoOutput' },
        @{ Category = 'BadResults' }
    ) {
        $v = $byCategory[$Category]
        $v.Category | Should -Be $Category
        (Get-RunReport -Verdicts @($someNotRun, $v) -Category 'RequiresExternalSoftware').ExitCode | Should -Be 1
    }

    # A repository in which nothing ran and nothing was all excluded. Reaches the "no test ran"
    # check with no all-excluded project, which must not throw.
    Context 'a report whose only project is <Category>' -ForEach @(
        @{ Category = 'NoTests';      Row = '| Empty | 0 | 0 | 0 | 0 | 0 | no tests |'; Message = '::error title=UnitTests::No test found in project(s): Empty . The project compiled but declares no test. Remove the project or add tests.' }
        @{ Category = 'NoneExecuted'; Row = '| none-executed-project | 2 | 0 | 0 | 2 | 0 | none executed |'; Message = '::error title=UnitTests::no tests executed in none-executed-project (2 discovered, 2 not run). The filter admitted these tests and none of them ran, so this project examined nothing. Tag them [Category("C")] if they need software a runner cannot have, otherwise fix what is stopping them.' }
        @{ Category = 'BuildFailed';  Row = '| Broken | - | - | - | - | - | build failed |'; Message = '::error title=UnitTests::Build failed for project(s): Broken' }
    ) {
        BeforeAll {
            $script:report = Get-RunReport -Verdicts @($byCategory[$Category]) -Category 'C'
        }

        It 'fails' {
            $report.ExitCode | Should -Be 1
        }

        It 'gives exactly its error, and no "no test ran" warning' {
            $report.Annotations | Should -Be @($Message)
        }

        It 'still writes the table' {
            $report.SummaryLines.Count | Should -Be 7
            $report.SummaryLines[6]    | Should -Be $Row
        }
    }

    It 'writes the table with one row per project, in order' {
        $s = (Get-RunReport -Verdicts @($built, $someNotRun) -Category 'X').SummaryLines
        $s[0..5] | Should -Be @('### Unit tests', '', 'Filter: `TestCategory!=X`', '',
            '| Project | Discovered | Ran | Excluded | Not run | Failed | Result |', '|---|---:|---:|---:|---:|---:|---|')
        $s[6] | Should -Be '| Broken | - | - | - | - | - | build failed |'
        $s[7] | Should -Be '| some-not-run-project | 2 | 1 | 0 | 1 | 0 | passed, some not run |'
    }

    It 'collapses the not-run names' {
        $s = (Get-RunReport -Verdicts @($someNotRun) -Category 'X').SummaryLines
        $s[-5..-1] | Should -Be @('<details><summary>Not run (1)</summary>', '', '- `some-not-run-project`: ThisOneIsInconclusive', '', '</details>')
    }

    It 'warns that no test ran only when every project that ran was all excluded' {
        $a = (Get-RunReport -Verdicts @($allExcluded) -Category 'C').Annotations
        $a | Should -Contain '::warning title=UnitTests::No test ran in this repository: every discovered test carries [Category("C")].'
        $b = (Get-RunReport -Verdicts @($allExcluded, $noneRan) -Category 'C').Annotations
        $b | Should -Not -Contain '::warning title=UnitTests::No test ran in this repository: every discovered test carries [Category("C")].'
    }

    It 'names each failing project in its error' {
        $a = (Get-RunReport -Verdicts @($built, $failing, $noneRan) -Category 'C').Annotations
        $a | Should -Contain '::error title=UnitTests::Build failed for project(s): Broken'
        $a | Should -Contain '::error title=UnitTests::Test runs failed for project(s): failing-project'
        @($a | Where-Object { $_ -like '::error title=UnitTests::no tests executed in none-executed-project (2 discovered, 2 not run)*' }).Count | Should -Be 1
    }
}
