# Retries a git call to a dependency's remote when, and only when, it failed reaching GitHub.
#
# A dropped connection during a lookup fails the whole check for a reason that has nothing to do
# with the change under review. git exits 128 both for that and for a repository that does not
# exist or that the token cannot read, so the exit code cannot tell them apart and git's message
# is classified instead. Anything not recognised as a network error fails on the first attempt.

$script:TransientGitPatterns = @(
    'Could not resolve host'
    'timed out'
    'Connection (was )?reset'
    'Connection refused'
    'Failed to connect to'
    'Could not connect to server'
    'Empty reply from server'
    'early EOF'
    'RPC failed'
    'The requested URL returned error: (429|5\d\d)'
    'the remote end hung up unexpectedly'
    # TLS handshakes cut off part way. A certificate the runner rejects matches none of these,
    # so it fails on the first attempt.
    'schannel: failed to receive handshake'
    'SSL/TLS connection failed'
    'SSL_ERROR_SYSCALL'
)

# Checked first, so "RPC failed; HTTP 403" is not retried on the strength of "RPC failed".
$script:PermanentGitPatterns = @(
    'Repository not found'
    "repository '[^']*' not found"
    'Authentication failed'
    'could not read Username'
    'Permission denied'
    '(HTTP|returned error:) 4(?!08|29)\d\d'
)

function Test-TransientGitFailure {
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Output)

    if ($Output.Count -eq 0) { return $false }
    $permanent = ($script:PermanentGitPatterns -join '|')
    if (@($Output | Where-Object { $_ -match $permanent }).Count -gt 0) { return $false }
    $transient = ($script:TransientGitPatterns -join '|')
    return @($Output | Where-Object { $_ -match $transient }).Count -gt 0
}

function Invoke-GitWithRetry {
    param(
        [Parameter(Mandatory)][string]$What,
        [Parameter(Mandatory)][string]$OwnerRepo,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$MaxAttempts = 3,
        [int[]]$DelaySeconds = @(15, 45)
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $stdout = [System.Collections.Generic.List[string]]::new()
        $stderr = [System.Collections.Generic.List[string]]::new()
        & git @Arguments 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $stderr.Add("$_") } else { $stdout.Add("$_") }
        }
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0) {
            $stderr | ForEach-Object { Write-Host $_ }
            return $stdout.ToArray()
        }

        $transient = Test-TransientGitFailure -Output $stderr.ToArray()

        if ($transient -and $attempt -lt $MaxAttempts) {
            Write-Host "git $What for '$OwnerRepo' failed on a network error (attempt $attempt of $MaxAttempts); its output follows, indented."
            $stderr | ForEach-Object { Write-Host "    $_" }
            $delay = $DelaySeconds[[Math]::Min($attempt - 1, $DelaySeconds.Count - 1)]
            Write-Host "::warning title=Dependencies::git $What for $OwnerRepo failed on a network error reaching GitHub (attempt $attempt of $MaxAttempts). Retrying in $delay seconds."
            Start-Sleep -Seconds $delay
            continue
        }

        $stderr | ForEach-Object { Write-Host $_ }
        $detail = @($stderr | Where-Object { $_ -match '^(fatal|error):' }) | Select-Object -Last 1
        $suffix = if ($detail) { " git said: $detail" } else { '' }

        if ($transient) {
            throw "git $What failed for '$OwnerRepo' after $MaxAttempts attempts, each on a network error reaching GitHub, so which commit to build could not be decided. This is not a fault in the change under review; re-run the check.$suffix"
        }
        throw "git $What failed for '$OwnerRepo' (exit $exitCode), so which commit to build could not be decided.$suffix"
    }
}
