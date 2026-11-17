# Invoke-DotnetRestore.Tests.ps1: which restore failures are retried.
#
# The property: a dropped connection to the feed is retried, and anything else fails on the
# first attempt. The transient lines are taken from a real ci-build failure; the genuine ones are
# real restore errors from the same fleet.
#
# Run locally:  pwsh -Command "Invoke-Pester .github/scripts/tests -Output Detailed"
# Run in CI:    lint-workflows.yml, the powershell-tests job.

BeforeAll {
    . (Join-Path $PSScriptRoot '../Invoke-DotnetRestore.ps1')

    $script:sdk = 'C:\Program Files\dotnet\sdk\8.0.427'
    $script:sln = '[C:\bhom-deps\BHoM_Engine\BHoM_Engine.sln]'
}

Describe 'Test-TransientRestoreFailure' {

    Context 'retried' {

        It 'a nupkg download cut off by the remote host' {
            $output = @(
                "  Restored C:\bhom-deps\BHoM_Engine\Spatial_Engine\Spatial_Engine.csproj (in 9 ms)."
                "  An error occurred while sending the request."
                "$sdk\NuGet.targets(174,5): error : Failed to download package 'Microsoft.CSharp.4.5.0' from 'https://api.nuget.org/v3-flatcontainer/microsoft.csharp/4.5.0/microsoft.csharp.4.5.0.nupkg'. $sln"
                "$sdk\NuGet.targets(174,5): error : An error occurred while sending the request. $sln"
                "$sdk\NuGet.targets(174,5): error :   Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.. $sln"
                "$sdk\NuGet.targets(174,5): error : The feed 'nuget.org [https://api.nuget.org/v3/index.json]' lists package 'Microsoft.CSharp.4.5.0' but multiple attempts to download the nupkg have failed. $sln"
                "  Failed to restore C:\bhom-deps\BHoM_Engine\Security_Engine\Security_Engine.csproj (in 16.94 sec)."
            )
            Test-TransientRestoreFailure -Output $output | Should -BeTrue
        }

        It 'a transport error in output that contains a blank line' {
            $output = @("C:\x\x.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.", '')
            Test-TransientRestoreFailure -Output $output | Should -BeTrue
        }

        It 'the service index unreachable' {
            $output = @("C:\x\x.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json.")
            Test-TransientRestoreFailure -Output $output | Should -BeTrue
        }

        It 'a 503 from the feed' {
            $output = @("$sdk\NuGet.targets(174,5): error : Response status code does not indicate success: 503 (Service Unavailable). $sln")
            Test-TransientRestoreFailure -Output $output | Should -BeTrue
        }
    }

    Context 'not retried' {

        It 'a package reference with no version' {
            $output = @("D:\a\R\R\P\P.csproj : error NU1015: The following PackageReference item(s) do not have a version specified: System.IO.Compression [D:\a\R\R\R.sln]")
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }

        It 'a target framework the SDK cannot build' {
            $output = @("$sdk\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.TargetFrameworkInference.targets(166,5): error NETSDK1045: The current .NET SDK does not support targeting .NET 10.0. $sln")
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }

        It 'a package that does not exist' {
            $output = @("C:\x\x.csproj : error NU1101: Unable to find package Not.A.Package. No packages exist with this id in source(s): nuget.org")
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }

        It 'a genuine error alongside a transport error' {
            $output = @(
                "$sdk\NuGet.targets(174,5): error : An error occurred while sending the request. $sln"
                "C:\x\x.csproj : error NU1101: Unable to find package Not.A.Package. No packages exist with this id in source(s): nuget.org"
            )
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }

        # NU1301 covers any feed NuGet cannot use. The shape is the one a GitHub-hosted runner
        # printed for a 503; the reasons are the ones a restore gives for a feed that is wrong.
        It 'a feed answering <Status>' -TestCases @(
            @{ Status = '404 (Not Found)' }
            @{ Status = '401 (Unauthorized)' }
            @{ Status = '403 (Forbidden)' }
        ) {
            param($Status)
            $output = @(
                "C:\x\x.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/no-such-index.json. [C:\x\x.sln]"
                "C:\x\x.csproj : error NU1301:   Response status code does not indicate success: $Status. [C:\x\x.sln]"
            )
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }

        It 'a local source that does not exist' {
            $output = @("C:\x\x.csproj : error NU1301: The local source 'C:\no\such\feed' doesn't exist. [C:\x\x.sln]")
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }

        It 'a non-zero exit with no error line at all' {
            $output = @("  Determining projects to restore...", "  An error occurred while sending the request.")
            Test-TransientRestoreFailure -Output $output | Should -BeFalse
        }
    }
}

Describe 'Invoke-DotnetRestoreWithRetry' {

    BeforeAll {
        $script:transient = "$sdk\NuGet.targets(174,5): error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json. $sln"
        $script:genuine   = "$sdk\NuGet.targets(174,5): error NU1101: Unable to find package Not.A.Package. No packages exist with this id in source(s): nuget.org $sln"
    }

    BeforeEach {
        # Fresh per test: the script keeps "the feed is down" state for the rest of a run.
        . (Join-Path $PSScriptRoot '../Invoke-DotnetRestore.ps1')
        $script:hostLines = [System.Collections.Generic.List[string]]::new()
        $script:exitCodes = [System.Collections.Generic.Queue[int]]::new()
        $script:outputs   = [System.Collections.Generic.Queue[string]]::new()
        Mock Write-Host { $script:hostLines.Add("$Object") }
        Mock Start-Sleep { }
        Mock dotnet {
            $line = $script:outputs.Dequeue()
            $global:LASTEXITCODE = $script:exitCodes.Dequeue()
            $line
        }
    }

    It 'retries a transport failure, as a warning, then succeeds' {
        $script:outputs.Enqueue($transient); $script:exitCodes.Enqueue(1)
        $script:outputs.Enqueue('  Restored x.csproj'); $script:exitCodes.Enqueue(0)

        { Invoke-DotnetRestoreWithRetry -Target 'x.sln' } | Should -Not -Throw

        Should -Invoke dotnet -Times 2 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' } | Should -HaveCount 1
        $hostLines | Where-Object { $_ -like '::error*' }   | Should -HaveCount 0
        # The retried attempt's output is indented, which sets it apart from the final attempt in the log.
        $hostLines | Where-Object { $_ -eq $transient }      | Should -HaveCount 0
        $hostLines | Where-Object { $_ -eq "    $transient" } | Should -HaveCount 1
    }

    It 'fails a genuine error on the first attempt, printed unindented' {
        $script:outputs.Enqueue($genuine); $script:exitCodes.Enqueue(1)

        { Invoke-DotnetRestoreWithRetry -Target 'x.sln' } | Should -Throw 'dotnet restore failed for x.sln'

        Should -Invoke dotnet -Times 1 -Exactly
        Should -Invoke Start-Sleep -Times 0 -Exactly
        $hostLines | Where-Object { $_ -eq $genuine }   | Should -HaveCount 1
        $hostLines | Where-Object { $_ -like '::warning*' } | Should -HaveCount 0
    }

    It 'keeps a retried attempt out of the dotnet problem matcher' {
        # setup-dotnet's matcher, as registered by the pinned version. It only matches from the
        # first column, which is what the indent relies on.
        $matcher = '^([^\s].*)\((\d+)(?:,\d+|,\d+,\d+)?\):\s+(error|warning)\s+([a-zA-Z]+(?<!MSB)\d+):\s*(.*?)\s+\[(.*?)\]$'
        $transient | Should -Match $matcher
        $script:outputs.Enqueue($transient); $script:exitCodes.Enqueue(1)
        $script:outputs.Enqueue('  Restored x.csproj'); $script:exitCodes.Enqueue(0)

        Invoke-DotnetRestoreWithRetry -Target 'x.sln'

        $hostLines | Where-Object { $_ -match $matcher } | Should -HaveCount 0
    }

    It 'fails a feed that answers 404 on the first attempt, without blaming the network' {
        $script:outputs.Enqueue("C:\x\x.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/no-such-index.json. [C:\x\x.sln]`nC:\x\x.csproj : error NU1301:   Response status code does not indicate success: 404 (Not Found). [C:\x\x.sln]")
        $script:exitCodes.Enqueue(1)

        $thrown = $null
        try { Invoke-DotnetRestoreWithRetry -Target 'x.sln' } catch { $thrown = $_.Exception.Message }

        $thrown | Should -Be 'dotnet restore failed for x.sln'
        Should -Invoke dotnet -Times 1 -Exactly
        Should -Invoke Start-Sleep -Times 0 -Exactly
    }

    It 'makes one attempt per restore for the rest of the run once an earlier one used every attempt' {
        foreach ($i in 1..3) { $script:outputs.Enqueue($transient); $script:exitCodes.Enqueue(1) }
        { Invoke-DotnetRestoreWithRetry -Target 'a.sln' } | Should -Throw '*after 3 attempts*'
        Should -Invoke dotnet -Times 3 -Exactly

        # The feed is evidently down: the next dependency's restore is not retried.
        $script:outputs.Enqueue($transient); $script:exitCodes.Enqueue(1)
        { Invoke-DotnetRestoreWithRetry -Target 'b.sln' } | Should -Throw '*not retried because an earlier restore*not a fault*'
        Should -Invoke dotnet -Times 4 -Exactly

        # A genuine error is still reported as one, not as the outage.
        $script:outputs.Enqueue($genuine); $script:exitCodes.Enqueue(1)
        { Invoke-DotnetRestoreWithRetry -Target 'c.sln' } | Should -Throw 'dotnet restore failed for c.sln'

        # A later success, which may not have touched the feed, leaves the flag set.
        $script:outputs.Enqueue('  Restored d.csproj'); $script:exitCodes.Enqueue(0)
        Invoke-DotnetRestoreWithRetry -Target 'd.sln'
        $script:RestoreFeedUnreachable | Should -BeTrue
        $script:outputs.Enqueue($transient); $script:exitCodes.Enqueue(1)
        { Invoke-DotnetRestoreWithRetry -Target 'e.sln' } | Should -Throw '*not retried because an earlier restore*'
        Should -Invoke dotnet -Times 7 -Exactly
        Should -Invoke Start-Sleep -Times 2 -Exactly
    }

    It 'gives up after the last attempt and prints that attempt unindented' {
        foreach ($i in 1..3) { $script:outputs.Enqueue($transient); $script:exitCodes.Enqueue(1) }

        { Invoke-DotnetRestoreWithRetry -Target 'x.sln' } | Should -Throw '*after 3 attempts*'

        Should -Invoke dotnet -Times 3 -Exactly
        $hostLines | Where-Object { $_ -like '::warning*' }   | Should -HaveCount 2
        $hostLines | Where-Object { $_ -eq "    $transient" } | Should -HaveCount 2
        $hostLines | Where-Object { $_ -eq $transient }       | Should -HaveCount 1
    }
}
