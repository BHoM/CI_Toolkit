# fail-open-guards.Tests.ps1 — assertions over the four places where an absent or unreadable
# input used to narrow a check's scope without saying so.
#
# Same shape as the sibling action test files: properties of an action as a whole that a reader
# cannot check from any one place in it. The property here is the one ci-versioning already
# holds and these four did not. An input goes missing, the check examines less than it claims,
# and the result is reported as a pass. A warning or an error is what separates that from a
# healthy run, so each guard is asserted to produce one.
#
# These are structural rather than behavioural because all four live in embedded PowerShell or
# in scripts whose failure path needs a broken dotnet to reach. The value is regression cover:
# if a guard is deleted, a test fails rather than a check going quiet again.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path

    function Get-FileText([string]$RelativePath) {
        Get-Content (Join-Path $script:repoRoot $RelativePath) -Raw
    }

    $script:datasetTests = Get-FileText '.github/actions/ci-dataset-tests/action.yml'
    $script:resolveDeps  = Get-FileText '.github/actions/resolve-dependencies/action.yml'
    $script:prepRunner   = Get-FileText '.github/actions/prepare-runner/action.yml'
    $script:discoverSln  = Get-FileText '.github/actions/discover-solution/action.yml'
    $script:altConfigs   = Get-FileText '.github/scripts/Build-AltConfigs.ps1'
    $script:buildDeps    = Get-FileText '.github/actions/resolve-dependencies/scripts/Build-Dependencies.ps1'
}

Describe 'ci-dataset-tests: a skip on the dataset-only path is announced' {

    # The two sibling branches in the same step both emit a notice saying what they ran. This
    # branch wrote count=0 and said nothing, and it is reached only when the changed-file count
    # and the list file disagree, which is the one case worth announcing.
    It 'warns when the list file is absent despite a non-zero dataset count' {
        $datasetTests | Should -Match 'changed_datasets\.txt is absent' `
            -Because 'otherwise a silent skip is indistinguishable from nothing relevant having changed'
    }

    It 'warns rather than failing, because the fault is in the check and the run is still usable' {
        $branch = ($datasetTests -split 'Only dataset \.json files changed')[-1]
        $branch = ($branch -split '- name: ')[0]
        $branch | Should -Match '::warning'
        $branch | Should -Not -Match '::error'
    }
}

Describe 'resolve-dependencies: absent and empty are different facts' {

    # Both used to produce an empty keypart, and an empty keypart skips the cache restore and
    # the dependency build alike. A repository that declares dependencies would then compile
    # against whatever was already staged.
    It 'fails when the SHA file is missing, because the step that creates it cannot not have run' {
        $resolveDeps | Should -Match '_shas\.txt is missing'
    }

    It 'treats a present but empty file as the ordinary no-dependencies case' {
        $resolveDeps | Should -Match 'No dependencies declared'
    }

    It 'still creates the file unconditionally, which is what makes absence meaningful' {
        $resolveDeps | Should -Match 'New-Item -ItemType File\s+-Force -Path deps/_shas\.txt' `
            -Because 'the guard above rests on that step having no conditional'
    }

    It 'checks for absence before reading the file' {
        $guardIdx = $resolveDeps.IndexOf('_shas.txt is missing')
        $readIdx  = $resolveDeps.IndexOf("Get-Content 'deps/_shas.txt'")
        $guardIdx | Should -BeGreaterThan 0
        $guardIdx | Should -BeLessThan $readIdx
    }
}

Describe 'prepare-runner: an empty source tree cannot key the cache' {

    # Hashing nothing yields the digest of an empty string, which is a constant, so the key
    # stops discriminating and a binary built from different source can be restored. The
    # existing "Runner source:" line does not catch it: it prints a plausible hash either way.
    It 'fails when no files are found under the source directory' {
        $prepRunner | Should -Match 'No files found under'
    }

    It 'guards before computing the hash, not after' {
        $guardIdx = $prepRunner.IndexOf('No files found under')
        $hashIdx  = $prepRunner.IndexOf('ComputeHash')
        $guardIdx | Should -BeGreaterThan 0
        $guardIdx | Should -BeLessThan $hashIdx
    }

    It 'fails rather than warning, because there is no correct key for a missing source tree' {
        $prepRunner | Should -Match '::error title=PrepareRunner'
    }
}

Describe 'the non-SDK gate is not cleared by a failed enumeration' {

    # `dotnet sln list` with its stderr discarded yields an empty set on failure, and an empty
    # set passes a gate that is looking for offending projects. All three call sites now check
    # the exit code.
    It 'discover-solution checks the exit code' {
        $discoverSln | Should -Match 'dotnet sln list exited'
    }

    It 'Build-AltConfigs.ps1 checks the exit code' {
        $altConfigs | Should -Match 'dotnet sln list exited'
    }

    It 'Build-Dependencies.ps1 checks the exit code' {
        $buildDeps | Should -Match 'dotnet sln list exited'
    }

    # The check has to happen between the call and the filtering, or the empty set has already
    # been consumed by the time anything looks at it.
    It 'checks the exit code before filtering, at every call site' {
        foreach ($pair in @(
            @{ Name = 'discover-solution';     Text = $discoverSln },
            @{ Name = 'Build-AltConfigs';      Text = $altConfigs },
            @{ Name = 'Build-Dependencies';    Text = $buildDeps }
        )) {
            $checkIdx  = $pair.Text.IndexOf('dotnet sln list exited')
            $filterIdx = $pair.Text.IndexOf('legacyProjects')
            $checkIdx  | Should -BeGreaterThan 0 -Because "$($pair.Name) should check the exit code"
            $checkIdx  | Should -BeLessThan $filterIdx -Because "$($pair.Name) should check before filtering"
        }
    }
}
