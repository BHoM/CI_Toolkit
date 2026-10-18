# ci-compliance-action.Tests.ps1 — structural assertions over ci-compliance's action.yml.
#
# The same shape as the sibling file for ci-serialisation: properties of the action as a whole
# that a reader cannot check from any one place in it.
#
# The property here is where the runner's output ends up. The invocation is wrapped in a
# ::group::, which the GitHub UI collapses by default, so anything the runner prints is hidden
# until someone expands it. The job summary is the surface a reader actually sees, and today it
# carries a single line saying only whether the check passed. A run that examined every file and
# a run that examined none produce the same summary.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:actionPath = Join-Path $repoRoot '.github/actions/ci-compliance/action.yml'
    $script:lines = Get-Content $actionPath
    $script:text  = $lines -join "`n"
    $script:changedFiles = Get-Content (Join-Path $repoRoot '.github/actions/compute-changed-files/action.yml') -Raw
}

Describe 'ci-compliance action.yml' {

    Context 'the job summary says how much was examined' {

        # Stdout is not the surface. The runner's output is inside a collapsed group, so a
        # count printed there is invisible to a reader who does not already suspect something.
        It 'wraps the runner invocation in a collapsed group' {
            $text | Should -Match '::group::' -Because 'if this stops being true the reasoning below needs revisiting'
        }

        It 'carries the examined count into the summary, not only pass or fail' {
            $text | Should -Match '\$summary \+= \$coverage' -Because 'a run that examined every file and one that examined none must not produce the same summary'
        }

        # The count is the runner's, read back off one line it controls. If that line is ever
        # renamed on one side only, the summary silently loses the count rather than breaking,
        # so both ends of the contract are asserted here.
        It 'reads the count off the line the runner emits' {
            $text | Should -Match "match '\^Coverage: '"
        }
    }

    Context 'the summary names the engine that produced the verdict' {

        # The rules come from Test_Toolkit's branch rather than a pinned version, by decision.
        # That decision is only safe while the version is visible, and the summary is where
        # someone looking at a red actually looks. See #34.
        It 'names the Test_Toolkit commit alongside the coverage line' {
            $text | Should -Match 'Rules from Test_Toolkit'
        }

        It 'takes the commit from the step that already reads it for the cache key' {
            $text | Should -Match 'steps\.tt-sha\.outputs\.sha' -Because 'a second lookup could disagree with the one the cache key used, which is the one that decided which engine ran'
        }

        # An absent SHA is not nothing: it also means the cache key lost its Test_Toolkit
        # component on that run. Silence would read as "no engine involved".
        It 'says so when the commit was not recorded, rather than omitting the line' {
            $text | Should -Match 'commit not recorded'
        }
    }

    Context 'a self-skip says so' {

        # The coverage line only exists once the runner runs. On a skip it never does, so
        # without this the summary is empty and a green that had nothing to check reads the
        # same as a green that checked everything.
        It 'writes to the summary when the check is skipped' {
            $text | Should -Match 'compliance skipped'
        }

        It 'names the pathspec it searched' {
            $text | Should -Match 'PATTERNS' -Because 'the one way a skip is wrong is the pathspec missing a file that did change, and that is not diagnosable from "skipped" alone'
        }

        # Two different states both produce count=0. Saying "no files changed" when the run was
        # never a pull request would be a wrong statement, not just a vague one.
        It 'distinguishes an empty diff from a run that was never a pull request' {
            $text | Should -Match 'not-a-pull-request'
        }

        It 'takes that reason from the action that knows it' {
            $text | Should -Match 'steps\.changed\.outputs\.reason' -Because 'compute-changed-files is the only place the two cases are told apart'
        }

        # The other end of that contract. A consumer reading an output no producer sets gets an
        # empty string and falls through to the wrong branch, silently.
        It 'is an output compute-changed-files actually declares' {
            $changedFiles | Should -Match '(?m)^\s{2}reason:'
        }

        It 'sets a reason on every path out of compute-changed-files' {
            foreach ($r in 'not-a-pull-request', 'no-matching-files', 'files-changed') {
                $changedFiles | Should -Match "reason=$r"
            }
        }
    }
}
