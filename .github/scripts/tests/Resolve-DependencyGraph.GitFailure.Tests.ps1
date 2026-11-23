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

    BeforeEach {
        # Read and changed in place from mock bodies, which run in the resolver's script scope,
        # so it is referenced unqualified rather than as $script:.
        $script:state = @{ Lookups = 0; Warnings = [System.Collections.Generic.List[string]]::new() }
    }

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

    It 'fails, rather than building the base branch, when the preferred-branch lookup keeps failing' {
        # One request failing while the next succeeds is the ordinary shape of a network blip.
        # Before the check, the empty output read as "no such branch" and the dependency was
        # built from develop instead of the pull request's matching branch, with no error.
        $ws   = Join-Path $sandbox 'blip-ws'
        $root = Join-Path $sandbox 'blip-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        # Retry warnings would otherwise become annotations on this repository's own check.
        Mock Start-Sleep { }
        Mock Write-Host -ParameterFilter { "$Object" -like '::warning*' } -MockWith { }
        Mock git -ParameterFilter { $args -contains 'ls-remote' -and $args -contains $prefer } -MockWith {
            $global:LASTEXITCODE = 128
        }

        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Throw "*ls-remote --heads $prefer*after 3 attempts*could not be decided*"
        Should -Invoke git -ParameterFilter { $args -contains 'ls-remote' -and $args -contains $prefer } -Times 3 -Exactly
        Should -Invoke Write-Host -ParameterFilter { "$Object" -like '::warning*' } -Times 2 -Exactly
    }

    It 'fails a fresh clone that cannot reach the remote, naming the clone' {
        $ws   = Join-Path $sandbox 'fresh-ws'
        $root = Join-Path $sandbox 'fresh-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        Mock Start-Sleep { }
        Mock Write-Host -ParameterFilter { "$Object" -like '::warning*' } -MockWith { }
        Move-Item $bare "$bare.moved"
        # Real git output, so this also shows its stderr is captured and quoted.
        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Throw '*git clone failed*after 3 attempts*git said: fatal:*'
        Should -Invoke Start-Sleep -Times 2 -Exactly
        Should -Invoke Write-Host -ParameterFilter { "$Object" -like '::warning*' } -Times 2 -Exactly
    }

    It 'retries a lookup that failed once, then builds the requested branch' {
        $ws   = Join-Path $sandbox 'retry-ws'
        $root = Join-Path $sandbox 'retry-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        Mock Start-Sleep { }
        Mock Write-Host { if ("$Object" -like '::warning*') { $state.Warnings.Add("$Object") } }
        Mock git -ParameterFilter { $args -contains 'ls-remote' -and $args -contains $prefer } -MockWith {
            $state.Lookups++
            if ($state.Lookups -eq 1) {
                [System.Management.Automation.ErrorRecord]::new(
                    [System.Exception]::new("fatal: unable to access 'https://github.com/$dep.git/': Could not resolve host: github.com"),
                    'NativeCommandError', 'NotSpecified', $null)
                $global:LASTEXITCODE = 128
                return
            }
            & (Get-Command git -CommandType Application | Select-Object -First 1) @args
        }

        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Not -Throw
        $state.Lookups | Should -Be 2
        Should -Invoke Start-Sleep -Times 1 -Exactly
        $state.Warnings | Should -HaveCount 1
        Get-Content (Join-Path $ws 'deps/_selection.txt') | Should -Match "^$([regex]::Escape($dep))\|Dep\|$([regex]::Escape($prefer))\|"
    }

    It 'retries, then fails, a repository that does not exist' {
        # GitHub answers a missing repository, and one the token cannot read, the same way. Neither
        # is told apart from a network error: both are retried, then fail naming the repository.
        $ws   = Join-Path $sandbox 'missing-ws'
        $root = Join-Path $sandbox 'missing-clones'
        New-Item -ItemType Directory -Force -Path $ws | Out-Null
        Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

        Mock Start-Sleep { }
        Mock Write-Host -ParameterFilter { "$Object" -like '::warning*' } -MockWith { }
        Mock git -ParameterFilter { $args -contains 'clone' } -MockWith {
            foreach ($line in @('remote: Repository not found.', "fatal: repository 'https://github.com/$dep.git/' not found")) {
                [System.Management.Automation.ErrorRecord]::new([System.Exception]::new($line), 'NativeCommandError', 'NotSpecified', $null)
            }
            $global:LASTEXITCODE = 128
        }

        { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer $prefer -Fallback 'develop' } |
            Should -Throw "git clone failed for '$dep' after 3 attempts (exit 128)*not found"
        Should -Invoke git -ParameterFilter { $args -contains 'clone' } -Times 3 -Exactly
        Should -Invoke Start-Sleep -Times 2 -Exactly
        Should -Invoke Write-Host -ParameterFilter { "$Object" -like '::warning*' } -Times 2 -Exactly
    }

    Context 'neither the preferred nor the base branch exists, so the remote default is built' {

        It 'labels the selection with the default branch name' {
            $ws   = Join-Path $sandbox 'default-ws'
            $root = Join-Path $sandbox 'default-clones'
            New-Item -ItemType Directory -Force -Path $ws | Out-Null
            Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

            { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'no-such-branch' -Fallback 'no-such-base' } |
                Should -Not -Throw
            Get-Content (Join-Path $ws 'deps/_selection.txt') | Should -Match "^$([regex]::Escape($dep))\|Dep\|develop\|[0-9a-f]{40}$"
        }

        It 'still builds when the lookup that only names the default branch fails' {
            # ls-remote --symref runs after the default branch is already fetched and checked out,
            # and only supplies its name for the summary. Its failure must not fail the resolution.
            $ws   = Join-Path $sandbox 'symref-ws'
            $root = Join-Path $sandbox 'symref-clones'
            New-Item -ItemType Directory -Force -Path $ws | Out-Null
            Set-Content -Path (Join-Path $ws 'dependencies.txt') -Value $dep -Encoding utf8

            Mock git -ParameterFilter { $args -contains '--symref' } -MockWith { $global:LASTEXITCODE = 128 }

            { Invoke-Resolver -Workspace $ws -CloneRoot $root -Prefer 'no-such-branch' -Fallback 'no-such-base' } |
                Should -Not -Throw
            Should -Invoke git -ParameterFilter { $args -contains '--symref' } -Times 1 -Exactly
            Get-Content (Join-Path $ws 'deps/_selection.txt') | Should -Match "^$([regex]::Escape($dep))\|Dep\|\(remote default\)\|[0-9a-f]{40}$"
            # The step's exit code is the last native command's, so a leftover 128 would fail it.
            $global:LASTEXITCODE | Should -Be 0
        }
    }
}

Describe 'resolve-dependencies action: a resolver failure is annotated with its reason' {

    It 'turns the thrown message into an ::error annotation and exits 1' {
        # Runs the step's own script from action.yml against a stand-in resolver that throws, with
        # the preamble GitHub adds for shell: pwsh, so the wrapper is tested rather than its text.
        $actionYml = Join-Path $repoRoot '.github/actions/resolve-dependencies/action.yml'
        $lines = Get-Content $actionYml
        $start = [Array]::IndexOf($lines, '    - name: Resolve dependency graph')
        $start | Should -BeGreaterThan -1
        $run = $null
        for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^    - name:'; $i++) {
            if ($null -ne $run) { $run += ,($lines[$i] -replace '^        ', '') }
            elseif ($lines[$i] -eq '      run: |') { $run = @() }
        }
        $body = ($run -join "`n") -replace '\$\{\{[^}]*\}\}', ''

        $actionPath = Join-Path $sandbox 'step-action'
        New-Item -ItemType Directory -Force -Path (Join-Path $actionPath 'scripts') | Out-Null
        Set-Content -Path (Join-Path $actionPath 'scripts/Resolve-DependencyGraph.ps1') -Encoding utf8 -Value @'
param($DepsFile, $Mode, $Seeds, $AdditionalSeeds, $CloneRoot)
throw "git clone failed for 'Fake/Dep' (exit 128), 100% sure"
'@
        $step = Join-Path $sandbox 'step.ps1'
        Set-Content -Path $step -Encoding utf8 -Value ("`$ErrorActionPreference = 'stop'`n" + $body)

        $env:GITHUB_ACTION_PATH = $actionPath
        # In-process: exit inside a script file ends that script only and sets $LASTEXITCODE.
        try { $out = & $step *>&1 | ForEach-Object { "$_" } }
        finally { Remove-Item Env:GITHUB_ACTION_PATH }

        $LASTEXITCODE | Should -Be 1
        $out | Should -Contain "::error title=Dependencies::git clone failed for 'Fake/Dep' (exit 128), 100%25 sure"
        # The stack trace names the script and line the error came from.
        ($out -join "`n") | Should -Match 'Resolve-DependencyGraph\.ps1: line 2'
    }
}
