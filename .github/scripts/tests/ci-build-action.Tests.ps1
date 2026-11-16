# ci-build-action.Tests.ps1
#
# The restore in ci-build's build step reports a final failure as its own error annotation.
# Runs the step's script from action.yml, with the preamble GitHub adds for shell: pwsh, against
# a stand-in restore that throws, so the wrapper is tested rather than its text.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    $script:repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $script:sandbox  = Join-Path ([IO.Path]::GetTempPath()) ("ci-build-step-" + [Guid]::NewGuid().ToString('N'))
}

AfterAll {
    Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Describe 'ci-build: a restore failure is annotated with its reason' {

    It 'escapes the message into one ::error annotation and exits 1' {
        $lines = Get-Content (Join-Path $repoRoot '.github/actions/ci-build/action.yml')
        $start = [Array]::IndexOf($lines, '    - name: Build (Release)')
        $start | Should -BeGreaterThan -1
        $run = $null
        for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^    - name:'; $i++) {
            if ($null -ne $run) { $run += ,($lines[$i] -replace '^        ', '') }
            elseif ($lines[$i] -eq '      run: |') { $run = @() }
        }
        $body = ($run -join "`n") -replace '\$\{\{[^}]*\}\}', ''

        # The step dot-sources $GITHUB_ACTION_PATH/../../scripts/Invoke-DotnetRestore.ps1.
        $actionPath = Join-Path $sandbox 'actions/ci-build'
        New-Item -ItemType Directory -Force -Path $actionPath, (Join-Path $sandbox 'scripts') | Out-Null
        Set-Content -Path (Join-Path $sandbox 'scripts/Invoke-DotnetRestore.ps1') -Encoding utf8 -Value @'
function Invoke-DotnetRestoreWithRetry { param($Target) throw "dotnet restore failed, 100% sure`nsecond line" }
'@
        $step = Join-Path $sandbox 'step.ps1'
        Set-Content -Path $step -Encoding utf8 -Value ("`$ErrorActionPreference = 'stop'`n" + $body)

        $env:GITHUB_ACTION_PATH = $actionPath
        # In-process: exit inside a script file ends that script only and sets $LASTEXITCODE.
        try { $out = & $step *>&1 | ForEach-Object { "$_" } }
        finally { Remove-Item Env:GITHUB_ACTION_PATH }

        $LASTEXITCODE | Should -Be 1
        $out | Should -Contain '::error title=Restore::dotnet restore failed, 100%25 sure%0Asecond line'
    }
}
