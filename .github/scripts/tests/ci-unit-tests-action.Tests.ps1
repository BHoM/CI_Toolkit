# ci-unit-tests-action.Tests.ps1: behaviour of ci-unit-tests' discover step when a repository
# has no .ci/unit-tests.
#
# The step passes in that case, so the only thing separating "no tests here" from a run that
# examined something is what it writes. The step's script is lifted out of action.yml and run
# in an empty directory with the job files pointed at temporary files, so the assertions are on
# what it actually writes and how it exits. `exit` in a script file called with `&` returns from
# that script and sets $LASTEXITCODE, so it runs in this session rather than a child process.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $action   = Get-Content (Join-Path $repoRoot '.github/actions/ci-unit-tests/action.yml') -Raw

    # The run block of the discover step: every line after `run: |` indented deeper than the
    # step's keys, with that indentation removed.
    $lines = $action -split "`r?`n"
    $step  = [Array]::FindIndex([string[]]$lines, [Predicate[string]]{ param($l) $l -match '^\s+- name: Discover test projects\s*$' })
    $run   = [Array]::FindIndex([string[]]$lines, $step, [Predicate[string]]{ param($l) $l -match '^\s+run: \|\s*$' })
    $body  = @()
    for ($i = $run + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\S' -or ($lines[$i] -match '^( *)\S' -and $Matches[1].Length -le 6)) { break }
        $body += $lines[$i]
    }
    $indent = ($body | Where-Object { $_ -match '\S' } | ForEach-Object { ($_ -replace '^( *).*', '$1').Length } | Measure-Object -Minimum).Minimum
    $script:stepScript = ($body | ForEach-Object { if ($_.Length -ge $indent) { $_.Substring($indent) } else { '' } }) -join "`n"

    function Invoke-DiscoverStep {
        $dir = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $dir | Out-Null
        try {
            $file = Join-Path $dir 'step.ps1'
            Set-Content -Path $file -Value $script:stepScript -Encoding utf8
            $saved = @{ S = $env:GITHUB_STEP_SUMMARY; O = $env:GITHUB_OUTPUT; P = $env:PROJECT_LIST }
            $env:GITHUB_STEP_SUMMARY = Join-Path $dir 'summary.md'
            $env:GITHUB_OUTPUT       = Join-Path $dir 'output.txt'
            $env:PROJECT_LIST        = Join-Path $dir 'projects.txt'
            Push-Location $dir
            try {
                $global:LASTEXITCODE = $null
                $log  = & $file 6>&1 2>&1 | Out-String
                $exit = $LASTEXITCODE
            } finally {
                Pop-Location
                $env:GITHUB_STEP_SUMMARY = $saved.S; $env:GITHUB_OUTPUT = $saved.O; $env:PROJECT_LIST = $saved.P
            }
            $read = { param($p) if (Test-Path $p) { Get-Content $p -Raw } else { '' } }
            [pscustomobject]@{
                ExitCode = $exit
                Log      = $log
                Summary  = & $read (Join-Path $dir 'summary.md')
                Output   = & $read (Join-Path $dir 'output.txt')
            }
        } finally {
            Remove-Item $dir -Recurse -Force
        }
    }
}

Describe 'ci-unit-tests: a repository with no .ci/unit-tests' {

    BeforeAll {
        $script:r = Invoke-DiscoverStep
    }

    It 'found the discover step in action.yml' {
        $stepScript | Should -Match 'Test-Path \$root'
    }

    It 'passes' {
        $r.ExitCode | Should -Be 0 -Because $r.Log
    }

    It 'says in the job summary that nothing ran' {
        $r.Summary | Should -Match 'has no unit tests' -Because 'a green check should not read as tests having run'
    }

    It 'tells later steps there is nothing to run' {
        $r.Output | Should -Match '(?m)^found=false\s*$'
    }
}
