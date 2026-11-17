# Invoke-GitWithRetry.Tests.ps1: which git failures are retried.
#
# The property: a git call that could not reach GitHub is retried, and anything else, above all a
# repository that does not exist or cannot be read, fails on the first attempt. git exits 128 for
# both, so these cases differ only in the message.
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

Describe 'Test-TransientGitFailure' {

    Context 'retried' {

        It '<Name>' -TestCases @(
            # The first two are from production ci-serialisation and ci-versioning logs.
            @{ Name = 'connection refused at the TCP level'; Line = "fatal: unable to access '$url': Failed to connect to github.com:443 after 21055 ms: Could not connect to server" }
            @{ Name = 'empty reply';                         Line = "fatal: unable to access '$url': Empty reply from server" }
            @{ Name = 'DNS failure';                         Line = "fatal: unable to access '$url': Could not resolve host: github.com" }
            @{ Name = 'connection reset, Windows wording';   Line = "fatal: unable to access '$url': Recv failure: Connection was reset" }
            @{ Name = 'connection reset';                    Line = "error: RPC failed; curl 56 Recv failure: Connection reset by peer" }
            @{ Name = 'timed out';                           Line = "fatal: unable to access '$url': Failed to connect to github.com port 443 after 21 ms: Timed out" }
            @{ Name = 'early EOF';                           Line = 'fatal: early EOF' }
            @{ Name = 'a 503';                               Line = "fatal: unable to access '$url': The requested URL returned error: 503" }
            @{ Name = 'a 429';                               Line = "fatal: unable to access '$url': The requested URL returned error: 429" }
            @{ Name = 'the remote hanging up';               Line = 'fatal: the remote end hung up unexpectedly' }
            @{ Name = 'a Windows TLS handshake cut off';     Line = "fatal: unable to access '$url': schannel: failed to receive handshake, SSL/TLS connection failed" }
            @{ Name = 'an OpenSSL handshake cut off';        Line = "fatal: unable to access '$url': OpenSSL SSL_connect: SSL_ERROR_SYSCALL in connection to github.com:443" }
        ) {
            param($Name, $Line)
            Test-TransientGitFailure -Output @($Line) | Should -BeTrue
        }
    }

    Context 'not retried' {

        It '<Name>' -TestCases @(
            @{ Name = 'repository not found';       Lines = @('remote: Repository not found.', "fatal: repository '$url' not found") }
            @{ Name = 'authentication failed';      Lines = @("fatal: Authentication failed for '$url'") }
            @{ Name = 'no credentials';             Lines = @("fatal: could not read Username for 'https://github.com': terminal prompts disabled") }
            @{ Name = 'a 403 inside an RPC failure'; Lines = @('error: RPC failed; HTTP 403 curl 22 The requested URL returned error: 403', 'fatal: expected flush after ref listing') }
            @{ Name = 'a 404';                      Lines = @("fatal: unable to access '$url': The requested URL returned error: 404") }
            @{ Name = 'a 403, then the remote hanging up'; Lines = @('error: RPC failed; HTTP 403 curl 22 The requested URL returned error: 403', 'fatal: the remote end hung up unexpectedly') }
            @{ Name = 'a certificate the runner rejects';  Lines = @("fatal: unable to access '$url': schannel: next InitializeSecurityContext failed: SEC_E_UNTRUSTED_ROOT (0x80090325) - The certificate chain was issued by an authority that is not trusted.") }
            @{ Name = 'an unrecognised failure';    Lines = @("fatal: 'C:/remotes/Fake/Dep.git' does not appear to be a git repository", 'fatal: Could not read from remote repository.') }
            @{ Name = 'no output at all';           Lines = @() }
            @{ Name = 'a blank line only';          Lines = @('') }
        ) {
            param($Name, $Lines)
            Test-TransientGitFailure -Output $Lines | Should -BeFalse
        }
    }
}

Describe 'Invoke-GitWithRetry' {

    BeforeAll {
        $script:transient = "fatal: unable to access '$url': Could not resolve host: github.com"
        $script:notFound  = "fatal: repository '$url' not found"
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
    }

    It 'retries a network failure, as a warning, then returns the result' {
        Add-Attempt -Out '' -Err $transient -Exit 128
        Add-Attempt -Out 'abc123	refs/heads/develop' -Err '' -Exit 0

        $result = Invoke-GitWithRetry 'ls-remote --heads develop' 'Fake/Dep' @('ls-remote', '--heads', 'origin', 'develop')

        $result | Should -Be 'abc123	refs/heads/develop'
        Should -Invoke git -Times 2 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' } | Should -HaveCount 1
        $hostLines | Where-Object { $_ -like '::error*' }   | Should -HaveCount 0
        $hostLines | Where-Object { $_ -eq "    $transient" } | Should -HaveCount 1
    }

    It 'fails a missing repository on the first attempt' {
        Add-Attempt -Out '' -Err $notFound -Exit 128

        { Invoke-GitWithRetry 'clone' 'Fake/Dep' @('clone', $url, 'C:\x') } |
            Should -Throw "git clone failed for 'Fake/Dep' (exit 128)*git said: $notFound"

        Should -Invoke git -Times 1 -Exactly
        Should -Invoke Start-Sleep -Times 0 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' } | Should -HaveCount 0
    }

    It 'fails an unrecognised failure on the first attempt' {
        Add-Attempt -Out '' -Err '' -Exit 128

        { Invoke-GitWithRetry 'fetch develop' 'Fake/Dep' @('fetch', 'origin', 'develop') } |
            Should -Throw "git fetch develop failed for 'Fake/Dep' (exit 128), so which commit to build could not be decided."

        Should -Invoke git -Times 1 -Exactly
    }

    It 'gives up after the last attempt, naming the network error' {
        foreach ($i in 1..3) { Add-Attempt -Out '' -Err $transient -Exit 128 }

        { Invoke-GitWithRetry 'fetch develop' 'Fake/Dep' @('fetch', 'origin', 'develop') } |
            Should -Throw "*after 3 attempts, each on a network error*git said: $transient"

        Should -Invoke git -Times 3 -Exactly
        Should -Invoke Start-Sleep -Times 2 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' }   | Should -HaveCount 2
        $hostLines | Where-Object { $_ -eq "    $transient" } | Should -HaveCount 2
        $hostLines | Where-Object { $_ -eq $transient }       | Should -HaveCount 1
    }
}
