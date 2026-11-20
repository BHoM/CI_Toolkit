# Invoke-GitWithRetry.Tests.ps1: a git call that fails is retried, then fails loudly.
#
# The property: any non-zero exit is retried, each retried attempt is a warning, and the last
# failure throws with git's own message. A success returns stdout only.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    . (Join-Path $PSScriptRoot '../Invoke-GitWithRetry.ps1')

    $script:url = 'https://github.com/Fake/Dep.git/'

    # What `& git ... 2>&1` yields for a line git wrote to stderr.
    function New-StderrLine([string]$Text) {
        [System.Management.Automation.ErrorRecord]::new(
            [System.Exception]::new($Text), 'NativeCommandError', 'NotSpecified', $null)
    }
}

Describe 'Invoke-GitWithRetry' {

    BeforeAll {
        # From production ci-serialisation and ci-versioning logs.
        $script:network  = "fatal: unable to access '$url': Failed to connect to github.com:443 after 21055 ms: Could not connect to server"
        $script:notFound = "fatal: repository '$url' not found"
        function Add-Attempt([string]$Out, [string]$Err, [int]$Exit) {
            $script:stdout.Enqueue($Out); $script:stderr.Enqueue($Err); $script:exitCodes.Enqueue($Exit)
        }
    }

    BeforeEach {
        $script:hostLines = [System.Collections.Generic.List[string]]::new()
        $script:exitCodes = [System.Collections.Generic.Queue[int]]::new()
        $script:stderr    = [System.Collections.Generic.Queue[string]]::new()
        $script:stdout    = [System.Collections.Generic.Queue[string]]::new()
        Mock Write-Host { $script:hostLines.Add("$Object") }
        Mock Start-Sleep { }
        Mock git {
            $out = $script:stdout.Dequeue()
            $err = $script:stderr.Dequeue()
            if ($out) { $out }
            if ($err) { New-StderrLine $err }
            $global:LASTEXITCODE = $script:exitCodes.Dequeue()
        }
    }

    It 'returns stdout only, so a lookup result is not polluted by progress lines' {
        Add-Attempt -Out 'abc123	refs/heads/develop' -Err "From $url" -Exit 0

        $result = Invoke-GitWithRetry 'ls-remote --heads develop' 'Fake/Dep' @('ls-remote', '--heads', 'origin', 'develop')

        $result | Should -Be 'abc123	refs/heads/develop'
        $hostLines | Should -Contain "From $url"
        Should -Invoke Start-Sleep -Times 0 -Exactly
    }

    It 'retries a failure, as a warning, then returns the result' {
        Add-Attempt -Out '' -Err $network -Exit 128
        Add-Attempt -Out 'abc123	refs/heads/develop' -Err '' -Exit 0

        $result = Invoke-GitWithRetry 'ls-remote --heads develop' 'Fake/Dep' @('ls-remote', '--heads', 'origin', 'develop')

        $result | Should -Be 'abc123	refs/heads/develop'
        Should -Invoke git -Times 2 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' } | Should -HaveCount 1
        $hostLines | Where-Object { $_ -like '::error*' }   | Should -HaveCount 0
        $hostLines | Where-Object { $_ -eq "    $network" } | Should -HaveCount 1
    }

    It 'waits 15 then 45 seconds between attempts' {
        foreach ($i in 1..3) { Add-Attempt -Out '' -Err $network -Exit 128 }

        { Invoke-GitWithRetry 'fetch develop' 'Fake/Dep' @('fetch', 'origin', 'develop') } | Should -Throw

        Should -Invoke Start-Sleep -ParameterFilter { $Seconds -eq 15 } -Times 1 -Exactly
        Should -Invoke Start-Sleep -ParameterFilter { $Seconds -eq 45 } -Times 1 -Exactly
    }

    It 'gives up after the last attempt, quoting git, with that attempt printed unindented' {
        foreach ($i in 1..3) { Add-Attempt -Out '' -Err $notFound -Exit 128 }

        $thrown = $null
        try { Invoke-GitWithRetry 'clone' 'Fake/Dep' @('clone', $url, 'C:\x') } catch { $thrown = $_.Exception.Message }

        $thrown | Should -Be "git clone failed for 'Fake/Dep' after 3 attempts (exit 128), so which commit to build could not be decided. git said: $notFound"
        Should -Invoke git -Times 3 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' }   | Should -HaveCount 2
        $hostLines | Where-Object { $_ -eq "    $notFound" } | Should -HaveCount 2
        $hostLines | Where-Object { $_ -eq $notFound }       | Should -HaveCount 1
    }

    It 'gives up with no quote when git printed nothing' {
        foreach ($i in 1..3) { Add-Attempt -Out '' -Err '' -Exit 128 }

        { Invoke-GitWithRetry 'fetch develop' 'Fake/Dep' @('fetch', 'origin', 'develop') } |
            Should -Throw "git fetch develop failed for 'Fake/Dep' after 3 attempts (exit 128), so which commit to build could not be decided."
    }
}
