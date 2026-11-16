# Retries `dotnet restore` when, and only when, it failed talking to the feed.
#
# A restore pulls every package cold from nuget.org on a pull request's first run,
# so a dropped connection fails the whole check for a reason that has nothing to do with the
# change under review. NuGet already retries each request a few times, but within seconds; the
# retry here waits long enough for a transport fault to clear.
#
# A genuine restore failure (unknown package, missing version, unsupported target framework)
# must still fail on the first attempt. So the output is classified, and only an attempt whose
# every error line is a transport error is retried.
#
# NuGet reports any feed it cannot use as NU1301, whatever the reason, so the code alone does not
# mean the network. An HTTP 4xx answer other than 408 and 429, or a local source that does not
# exist, is a configuration fault: checked first, and never retried.

$script:TransientRestorePatterns = @(
    'NU1301'
    'An error occurred while sending the request'
    'Unable to read data from the transport connection'
    'forcibly closed by the remote host'
    'The SSL connection could not be established'
    'Unable to load the service index'
    'multiple attempts to download the nupkg have failed'
    'Failed to download package'
    'Response status code does not indicate success: (408|429|5\d\d)'
    'The operation was canceled'
    'A connection attempt failed because the connected party did not properly respond'
)

$script:PermanentRestorePatterns = @(
    'Response status code does not indicate success: 4(?!08|29)\d\d'
    "local source '[^']*' doesn't exist"
)

function Test-TransientRestoreFailure {
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Output)

    # MSBuild's diagnostic format, "<origin>: error <CODE>: <text>", with or without a code.
    $errorLines = @($Output | Where-Object { $_ -match ':\s*error\s*([A-Z]+\d+\s*)?:' })
    if ($errorLines.Count -eq 0) { return $false }

    $permanent = ($script:PermanentRestorePatterns -join '|')
    if (@($Output | Where-Object { $_ -match $permanent }).Count -gt 0) { return $false }

    $pattern = ($script:TransientRestorePatterns -join '|')
    foreach ($line in $errorLines) {
        if ($line -notmatch $pattern) { return $false }
    }
    return $true
}

function Invoke-DotnetRestoreWithRetry {
    param(
        [Parameter(Mandatory)][string]$Target,
        [int]$MaxAttempts = 3,
        [int[]]$DelaySeconds = @(15, 45)
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        # Buffered rather than streamed: whether this attempt's output is final is only known
        # once it exits, and a retried attempt is printed differently from the final one.
        $output = @(dotnet restore $Target 2>&1 | ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE

        $willRetry = $exitCode -ne 0 -and $attempt -lt $MaxAttempts -and
                     (Test-TransientRestoreFailure -Output $output)

        if ($willRetry) {
            # Indented so the retried attempt reads as one block, and so it is not annotated as
            # errors: setup-dotnet's problem matcher only matches MSBuild-format lines starting in
            # the first column, such as "NuGet.targets(174,5): error NU1301: ... [x.sln]". Without
            # the indent, those lines from an attempt that is then retried become error annotations.
            Write-Host "Restore attempt $attempt of $MaxAttempts failed on a network error; its output follows, indented."
            $output | ForEach-Object { Write-Host "    $_" }
        } else {
            $output | ForEach-Object { Write-Host $_ }
        }

        if ($exitCode -eq 0) { return }

        if (-not $willRetry) {
            if (Test-TransientRestoreFailure -Output $output) {
                throw "dotnet restore failed for $Target after $MaxAttempts attempts, each on a network error reaching the package feed. This is not a fault in the change under review; re-run the check."
            }
            throw "dotnet restore failed for $Target"
        }

        $delay = $DelaySeconds[[Math]::Min($attempt - 1, $DelaySeconds.Count - 1)]
        Write-Host "::warning title=Restore::dotnet restore for $Target failed on a network error reaching the package feed (attempt $attempt of $MaxAttempts). Retrying in $delay seconds."
        Start-Sleep -Seconds $delay
    }
}
