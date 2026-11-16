# Resolve-DependencyGraph.GitFailure.Tests.ps1
#
# A git call that cannot reach the remote must fail the resolution, not be read as an answer.
# git ls-remote prints nothing both when a branch is absent and when the remote is unreachable,
# so an unchecked failure fell through to the next branch choice and the dependency was built
# from a branch nobody asked for, and the check reported on it.
#
# Hermetic, same mechanism as Resolve-DependencyGraph.BaselineReuse.Tests.ps1: a local bare repo
# reached through git's insteadOf rewrite. A single failed lookup is a mocked git call; a remote
# that is down altogether is the bare repo moved aside.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:resolver = Join-Path $repoRoot '.github/actions/resolve-dependencies/scripts/Resolve-DependencyGraph.ps1'
    $script:sandbox  = Join-Path ([IO.Path]::GetTempPath()) ("depgraph-git-" + [Guid]::NewGuid().ToString('N'))

    $script:remoteRoot = Join-Path $sandbox 'remotes'
    New-Item -ItemType Directory -Force -Path $remoteRoot | Out-Null
    $script:insteadOfKey = 'url.file:///' + ($remoteRoot -replace '\\', '/') + '/.insteadOf'
    git config --global $insteadOfKey 'https://github.com/'

    $script:savedSummary = $env:GITHUB_STEP_SUMMARY
    $env:GITHUB_STEP_SUMMARY = Join-Path $sandbox 'summary.md'

    $script:dep    = 'Fake/Dep'
    $script:prefer = 'feature/dependency-side-change'
    $script:bare   = Join-Path $remoteRoot "$dep.git"

    $work = Join-Path $sandbox 'work'
    New-Item -ItemType Directory -Force -Path (Split-Path $bare) | Out-Null
    git init -q --bare $bare
    git init -q $work
    Push-Location $work
    try {
        git config user.email 't@t'; git config user.name 't'
        git symbolic-ref HEAD refs/heads/develop
        Set-Content -Path 'Value.cs' -Value '// base' -Encoding utf8
        git add -A; git commit -q -m 'base'
        git checkout -q -b $prefer
        Set-Content -Path 'Value.cs' -Value '// branch' -Encoding utf8
        git add -A; git commit -q -m 'branch'
        git remote add origin $bare
        git push -q origin develop $prefer
        git --git-dir=$bare symbolic-ref HEAD refs/heads/develop
    }
    finally { Pop-Location }

    function Invoke-Resolver {
        param([string]$Workspace, [string]$CloneRoot, [string]$Prefer, [string]$Fallback)

        New-Item -ItemType Directory -Force -Path (Join-Path $Workspace 'deps') | Out-Null
        New-Item -ItemType File -Force -Path (Join-Path $Workspace 'deps/_shas.txt') | Out-Null
        $env:PR_BRANCH              = $Prefer
        $env:BASE_BRANCH            = $Fallback
        $env:DEP_TOKEN              = ''
        $env:PREFER_BRANCH_EXPLICIT = 'false'

        Push-Location $Workspace
        try {
            & $resolver -DepsFile 'dependencies.txt' -Mode 'caller' -Seeds '' `
                        -AdditionalSeeds '' -CloneRoot $CloneRoot | Out-Null
        }
        finally { Pop-Location }
    }
}

AfterAll {
    git config --global --unset $insteadOfKey 2>$null
    $env:GITHUB_STEP_SUMMARY = $savedSummary
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'resolve-dependencies: git failures reaching the remote' {

    AfterEach {
        if (Test-Path "$bare.moved") { Move-Item "$bare.moved" $bare }
    }

    It 'resolves normally while the remote is reachable' {
        $ws   = Join-Path $sandbox 'ok-ws'
        $root = Join-Path $sandbox 'ok-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Not -Throw
    }

    It 'fails, rather than building the base branch, when only the preferred-branch lookup fails' {
        # One request failing while the next succeeds is the ordinary shape of a network blip.
        # Before the check, the empty output read as "no such branch" and the dependency was
        # built from develop instead of the pull request's matching branch, with no error.
        $ws   = Join-Path $sandbox 'blip-ws'
        $root = Join-Path $sandbox 'blip-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        Mock git -ParameterFilter { $args -contains 'ls-remote' -and $args -contains $prefer } -MockWith {
            $global:LASTEXITCODE = 128
        }

        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Throw "*ls-remote --heads $prefer*could not be decided*"
        Should -Invoke git -ParameterFilter { $args -contains 'ls-remote' -and $args -contains $prefer } -Times 1 -Exactly
    }

    It 'fails a fresh clone that cannot reach the remote, naming the clone' {
        $ws   = Join-Path $sandbox 'fresh-ws'
        $root = Join-Path $sandbox 'fresh-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        Move-Item $bare "$bare.moved"
        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Throw '*git clone failed*'
    }
}
