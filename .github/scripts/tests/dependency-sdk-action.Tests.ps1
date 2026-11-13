BeforeAll {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
    $actions = Join-Path $repoRoot '.github/actions'
}

Describe 'dependency SDK selection' {
    $cases = @(
        @{ Action = 'resolve-dependencies'; InputName = 'dotnet_version' }
        @{ Action = 'ci-build'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-compliance'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-dataset-tests'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-unit-tests'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-serialisation'; InputName = 'dotnet_version' }
        @{ Action = 'ci-versioning'; InputName = 'dotnet_version' }
    )

    It '<Action> defaults <InputName> to SDK 10' -TestCases $cases {
        param($Action, $InputName)
        $text = Get-Content (Join-Path $actions "$Action/action.yml") -Raw
        $pattern = '(?ms)^  ' + [regex]::Escape($InputName) + ":\s*\r?\n(?:(?!^  \w)[^\n]*\n)*?    default: ['`"]10\.0['`"]"
        $text | Should -Match $pattern
    }

    It '<Action> forwards <InputName> to the dependency resolver' -TestCases @(
        @{ Action = 'ci-build'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-compliance'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-dataset-tests'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-unit-tests'; InputName = 'deps_dotnet_version' }
        @{ Action = 'ci-serialisation'; InputName = 'dotnet_version' }
        @{ Action = 'ci-versioning'; InputName = 'dotnet_version' }
    ) {
        param($Action, $InputName)
        $text = Get-Content (Join-Path $actions "$Action/action.yml") -Raw
        $pattern = '(?s)resolve-dependencies@develop\s+with:(?:(?!\n    - name:).)*?dotnet_version:\s*\$\{\{\s*inputs\.' + [regex]::Escape($InputName) + '\s*\}\}'
        $text | Should -Match $pattern
    }

    It 'keys the assembly cache by the selected SDK' {
        $text = Get-Content (Join-Path $actions 'resolve-dependencies/action.yml') -Raw
        $text | Should -Match 'depsasm-\$\{\{ runner\.os \}\}-\$\{\{ inputs\.configuration \}\}-\$\{\{ steps\.sdk\.outputs\.version \}\}'
    }
}
