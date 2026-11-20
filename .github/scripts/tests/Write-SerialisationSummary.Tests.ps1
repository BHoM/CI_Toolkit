# Write-SerialisationSummary.Tests.ps1: Pester tests for the serialisation job-summary decision.
#
# The decision is a pure function over the step outcomes a workflow expression yields, which is
# why it was extracted: reaching these states for real needs a dependency closure, a build, a
# runner and, for half of them, a repository that fails serialisation. None of that is needed to
# check which summary a given set of outcomes produces.
#
# Every parameter is a string because that is what a workflow expression yields, and the empty
# string is meaningful: it is how a step that never ran is told apart from one that ran and
# failed. The tests pass empty strings deliberately wherever a real run would.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    . (Join-Path $repoRoot '.github/scripts/Write-SerialisationSummary.ps1')

    # The coverage figures a healthy branch leg reports. Values are shaped like a real run's
    # but carry no test weight beyond being non-empty.
    $script:coverage = @{
        Population = '6053'; Assemblies = '83'; Legs = '6'; Failures = '0'
    }
}

Describe 'Get-SerialisationSummary' {

    Context 'the states the five conditions used to enumerate' {

        It 'reports that serialisation never started when the dependency closure fails' {
            $out = Get-SerialisationSummary -DepsBranchConclusion 'failure'
            ($out -join "`n") | Should -Match 'Serialisation did not run'
            ($out -join "`n") | Should -Match 'Resolve dependencies \(branch\)'
            ($out -join "`n") | Should -Match 'not a serialisation finding'
        }

        It 'names the caller build instead when that is what failed' {
            $out = Get-SerialisationSummary -BuildBranchConclusion 'failure'
            ($out -join "`n") | Should -Match 'Build primary repo \(branch\)'
        }

        It 'reports a clean branch leg without reaching for a baseline' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Pass' @coverage
            ($out -join "`n") | Should -Match 'No serialisation failures \(Pass\)'
        }

        It 'passes the job when every failure is already on the base branch' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Error' `
                                            -CompareVerdict 'ok' @coverage
            ($out -join "`n") | Should -Match 'All are pre-existing on the base branch'
        }

        It 'reports a regression when the comparison found new failures' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Error' `
                                            -CompareVerdict 'regression' @coverage
            ($out -join "`n") | Should -Match 'Serialisation regression detected'
        }

        It 'reports a runner failure when the branch leg itself died' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'failure'
            ($out -join "`n") | Should -Match 'could not be verified'
            ($out -join "`n") | Should -Match 'not a defect in this pull request'
        }

        It 'reports a runner failure when the baseline was too broken to diff against' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Error' `
                                            -CompareVerdict 'baseline-unusable' @coverage
            ($out -join "`n") | Should -Match 'could not be verified'
        }
    }

    Context 'the states nobody enumerated, which produced no summary at all' {

        # Seven steps run between the caller build and the branch-leg serialisation run:
        # locating and building the verification solution, inferring its configuration, and
        # publishing the runner. A failure in any of them left the check red and the summary
        # blank, with nothing to say serialisation had not been attempted.
        It 'describes a failure before the branch leg ran, rather than saying nothing' {
            $out = Get-SerialisationSummary -DepsBranchConclusion 'success' -BuildBranchConclusion 'success'

            $out | Should -Not -BeNullOrEmpty -Because 'silence is what this replaced'
            ($out -join "`n") | Should -Match 'did not reach a verdict'
            ($out -join "`n") | Should -Match 'before serialisation ran'
        }

        # Six steps run between the branch leg reporting failures and the comparison: the base
        # checkout, a second dependency resolution, two builds, a configuration inference and
        # the baseline run.
        It 'describes a failure in the baseline leg, rather than saying nothing' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Error' @coverage

            ($out -join "`n") | Should -Match 'did not reach a verdict'
            ($out -join "`n") | Should -Match 'baseline leg'
            ($out -join "`n") | Should -Match 'after the branch leg reported failures'
        }

        It 'still produces a summary when it can identify nothing at all' {
            $out = Get-SerialisationSummary

            $out | Should -Not -BeNullOrEmpty
            ($out -join "`n") | Should -Match 'did not reach a verdict'
        }

        It 'says a stop is a CI failure and not a finding about the pull request' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Error' @coverage
            ($out -join "`n") | Should -Match 'not a finding about this pull request'
        }
    }

    Context 'coverage figures' {

        # A green summary that examined nothing and a green summary that examined thousands
        # read identically without these, which is the whole reason the runner reports them.
        It 'carries the figures through when the branch leg measured them' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Pass' @coverage
            ($out -join "`n") | Should -Match 'Objects exercised: 6053 across 83 loaded assemblies, 6 legs'
            ($out -join "`n") | Should -Match 'Failures reported by the runner: 0'
        }

        It 'omits them when the run never produced any, rather than printing empty ones' {
            $out = Get-SerialisationSummary -DepsBranchConclusion 'success'
            ($out -join "`n") | Should -Not -Match 'Objects exercised'
        }

        It 'omits them on the path where serialisation never started' {
            $out = Get-SerialisationSummary -DepsBranchConclusion 'failure'
            ($out -join "`n") | Should -Not -Match 'Objects exercised'
        }
    }

    Context 'shape' {

        It 'always returns lines, never a bare string' {
            foreach ($case in @(
                @{ DepsBranchConclusion = 'failure' }
                @{ BranchRunConclusion = 'success'; BranchStatus = 'Pass' }
                @{ BranchRunConclusion = 'failure' }
                @{}
            )) {
                $out = Get-SerialisationSummary @case
                ,$out | Should -BeOfType [System.Object[]] -Because 'the caller pipes this straight into Out-File'
            }
        }

        It 'heads every summary except the did-not-run case with the check name' {
            $out = Get-SerialisationSummary -BranchRunConclusion 'success' -BranchStatus 'Pass' @coverage
            $out[0] | Should -Be '### Serialisation'
        }
    }
}

Describe 'Get-BaselineFailureList' {

    # Shown when the job passes because every branch failure is pre-existing, so a reader can see
    # which items the base already failed without the run's artefacts, which expire.
    It 'lists every base failure one per line, repeats included, with the count' {
        $descriptions = @('BH.oM.A.Alpha', 'BH.oM.B.Beta<BH.oM.C.Gamma>', 'BH.oM.A.Alpha')
        $out = Get-BaselineFailureList -CompareVerdict 'ok' -Descriptions $descriptions

        $out[0] | Should -Be '<details><summary>Failing on the base branch (3), for comparison with the failures reported above</summary>'
        @($out | Where-Object { $_ -eq 'BH.oM.A.Alpha' }).Count | Should -Be 2
        @($out | Where-Object { $_ -eq 'BH.oM.B.Beta<BH.oM.C.Gamma>' }).Count | Should -Be 1
        $fence = [array]::IndexOf($out, '```')
        $out[($fence + 1)..($fence + 3)] | Should -Be $descriptions
        $out[$fence + 4] | Should -Be '```'
    }

    It 'adds no list on any other verdict' {
        foreach ($verdict in 'regression', 'baseline-unusable', '') {
            Get-BaselineFailureList -CompareVerdict $verdict -Descriptions @('BH.oM.A.Alpha') |
                Should -BeNullOrEmpty -Because "verdict '$verdict' is not a pre-existing pass"
        }
    }

    It 'adds no empty block when there are no entries' {
        Get-BaselineFailureList -CompareVerdict 'ok' -Descriptions @() | Should -BeNullOrEmpty
    }

    It 'returns lines, never a bare string' {
        $out = Get-BaselineFailureList -CompareVerdict 'ok' -Descriptions @('BH.oM.A.Alpha')
        ,$out | Should -BeOfType [System.Object[]] -Because 'the caller pipes this straight into Out-File'
    }
}

Describe 'Get-BaselineFailureDescriptions' {

    BeforeAll {
        function script:Write-Result([string]$Name, [string[]]$Descriptions) {
            $failures = @($Descriptions | ForEach-Object { @{ Description = $_; Message = 'm' } })
            $path = Join-Path $TestDrive $Name
            @{ Status = 'Error'; FailureCount = $failures.Count; Failures = $failures } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
            $path
        }
    }

    It 'returns nothing for a result with no failures' {
        $out = Get-BaselineFailureDescriptions -Path (Write-Result 'none.json' @())
        $out.Count | Should -Be 0
    }

    It 'keeps a single entry as a one-item list' {
        $out = Get-BaselineFailureDescriptions -Path (Write-Result 'one.json' @('BH.oM.A.Alpha'))
        ,$out | Should -BeOfType [System.Object[]]
        $out.Count | Should -Be 1
        $out[0] | Should -Be 'BH.oM.A.Alpha'
    }

    It 'returns every entry in order, repeats included' {
        $descriptions = @('BH.oM.A.Alpha', 'BH.oM.B.Beta', 'BH.oM.A.Alpha')
        $out = Get-BaselineFailureDescriptions -Path (Write-Result 'many.json' $descriptions)
        $out | Should -Be $descriptions
    }

    It 'warns and returns nothing when the file is missing' {
        Mock Write-Host {}
        $out = Get-BaselineFailureDescriptions -Path (Join-Path $TestDrive 'absent.json')
        $out.Count | Should -Be 0
        Should -Invoke Write-Host -Times 1 -Exactly -ParameterFilter { $Object -match '^::warning title=Serialisation::' }
    }

    It 'warns and returns nothing when the file is not valid JSON' {
        Mock Write-Host {}
        $path = Join-Path $TestDrive 'bad.json'
        Set-Content -LiteralPath $path -Value '{ not json' -Encoding utf8
        $out = Get-BaselineFailureDescriptions -Path $path
        $out.Count | Should -Be 0
        Should -Invoke Write-Host -Times 1 -Exactly -ParameterFilter { $Object -match '^::warning title=Serialisation::' }
    }
}
