# Retries a git call to a dependency's remote that exits non-zero.
#
# A dropped connection during a lookup fails the whole check for a reason that has nothing to do
# with the change under review. Every call made through this succeeds on a normal path: ls-remote
# exits 0 with no output for a branch that does not exist, and a fetch is only made for a ref
# ls-remote has just listed. So any failure is retried, without trying to tell a network error
# from a missing or unreadable repository by its message. Those fail too, after the retries.
#
# Same attempts and waits as the restore retry in Invoke-DotnetRestore.ps1.

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

        if ($attempt -lt $MaxAttempts) {
            Write-Host "git $What for '$OwnerRepo' failed (attempt $attempt of $MaxAttempts); its output follows, indented."
            $stderr | ForEach-Object { Write-Host "    $_" }
            $delay = $DelaySeconds[[Math]::Min($attempt - 1, $DelaySeconds.Count - 1)]
            Write-Host "::warning title=Dependencies::git $What for $OwnerRepo failed (exit $exitCode, attempt $attempt of $MaxAttempts). Retrying in $delay seconds."
            Start-Sleep -Seconds $delay
            continue
        }

        $stderr | ForEach-Object { Write-Host $_ }
        $detail = @($stderr | Where-Object { $_ -match '^(fatal|error):' }) | Select-Object -Last 1
        $suffix = if ($detail) { " git said: $detail" } else { '' }
        throw "git $What failed for '$OwnerRepo' after $MaxAttempts attempts (exit $exitCode), so which commit to build could not be decided.$suffix"
    }
}
