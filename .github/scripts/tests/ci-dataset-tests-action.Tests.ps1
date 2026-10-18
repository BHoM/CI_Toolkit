# ci-dataset-tests-action.Tests.ps1 — structural assertions over ci-dataset-tests' action.yml.
#
# Same shape as the sibling files for ci-compliance, ci-serialisation and ci-versioning:
# properties of the action as a whole that a reader cannot check from any one place in it.
#
# The property here is that the cached runner and the engine inside it cannot drift apart.
# DatasetTestRunner copies five Test_Toolkit assemblies into its output, so the cached artefact
# is a copy of the test engine rather than a pointer to it. If the cache key does not name the
# Test_Toolkit commit, an engine change is ignored until the cache expires on its own, and the
# job reports on a version nothing names. ci-compliance has keyed on it since it was written;
# this action did not. See #35.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:datasetTests = Get-Content (Join-Path $repoRoot '.github/actions/ci-dataset-tests/action.yml') -Raw
    $script:compliance   = Get-Content (Join-Path $repoRoot '.github/actions/ci-compliance/action.yml') -Raw
}

Describe 'ci-dataset-tests action.yml' {

    Context 'the runner cache cannot outlive a Test_Toolkit change' {

        It 'reads the Test_Toolkit SHA from the dependency metadata' {
            $datasetTests | Should -Match 'deps/_shas\.txt'
        }

        It 'puts that SHA in the runner cache key' {
            $datasetTests | Should -Match 'cache_prefix:\s*dataset-test-runner-tt\$\{\{\s*steps\.tt-sha\.outputs\.sha'
        }

        # A fixed prefix is the defect, and it is the shape the key had before #35. Asserted
        # as an absence because the positive test above would still pass if a second,
        # unkeyed prepare-runner call were added later.
        #
        # (?m) is load-bearing. .NET anchors $ to the end of the whole string, not to each
        # line, so without it this pattern matches nothing and the assertion passes whatever
        # the file says. Caught by reverting the fix and watching this one stay green.
        It 'leaves no prepare-runner call on a fixed dataset-test prefix' {
            $datasetTests | Should -Not -Match '(?m)cache_prefix:[ \t]*dataset-test-runner[ \t]*\r?$'
        }

        # The step that reads the SHA has to run under the same condition as the step that
        # uses it, or the key silently loses its Test_Toolkit component on the paths where
        # the job does run.
        It 'gates the SHA read on the same condition as the rest of the job' {
            $block = ($datasetTests -split 'Read Test_Toolkit SHA for cache key')[-1]
            $block = ($block -split '- name: ')[0]
            $block | Should -Match "steps\.scope\.outputs\.count != '0'"
        }
    }

    Context 'it matches the action that already got this right' {

        # Both actions read the same file, the same way, for the same reason. If one is
        # changed the other should be too, and this is the cheapest thing that notices.
        It 'reads the SHA the same way ci-compliance does' {
            $pattern = "grep '\^\`$\{\{ inputs\.test_toolkit_repo \}\} ' deps/_shas\.txt"
            $datasetTests | Should -Match $pattern
            $compliance   | Should -Match $pattern
        }
    }
}
