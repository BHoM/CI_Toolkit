# Retries `dotnet restore` when, and only when, it failed talking to the feed.
#
# A dependency restore pulls every package cold from nuget.org on a pull request's first run,
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
        $output = [System.Collections.Generic.List[string]]::new()
        dotnet restore $Target 2>&1 | ForEach-Object {
            $line = "$_"
            Write-Host $line
            $output.Add($line)
        }
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0) { return }

        if (-not (Test-TransientRestoreFailure -Output $output.ToArray())) {
            throw "dotnet restore failed for $Target"
        }

        if ($attempt -eq $MaxAttempts) {
            throw "dotnet restore failed for $Target after $MaxAttempts attempts, each on a network error reaching the package feed. This is not a fault in the change under review; re-run the check."
        }

        $delay = $DelaySeconds[[Math]::Min($attempt - 1, $DelaySeconds.Count - 1)]
        Write-Host "::warning title=Restore::dotnet restore for $Target failed on a network error reaching the package feed (attempt $attempt of $MaxAttempts). Retrying in $delay seconds."
        Start-Sleep -Seconds $delay
    }
}
