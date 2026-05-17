Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

$rules = @{
    'src/Vessel.Domain/Vessel.Domain.csproj' = @()
    'src/Vessel.Shared/Vessel.Shared.csproj' = @()
    'src/Vessel.Application/Vessel.Application.csproj' = @(
        'src/Vessel.Domain/Vessel.Domain.csproj',
        'src/Vessel.Shared/Vessel.Shared.csproj'
    )
    'src/Vessel.Infrastructure/Vessel.Infrastructure.csproj' = @(
        'src/Vessel.Application/Vessel.Application.csproj',
        'src/Vessel.Domain/Vessel.Domain.csproj',
        'src/Vessel.Shared/Vessel.Shared.csproj'
    )
    'src/Vessel.Web/Vessel.Web.csproj' = @(
        'src/Vessel.Application/Vessel.Application.csproj',
        'src/Vessel.Infrastructure/Vessel.Infrastructure.csproj',
        'src/Vessel.Shared/Vessel.Shared.csproj'
    )
}

$failures = New-Object System.Collections.Generic.List[string]

foreach ($project in $rules.Keys) {
    $projectPath = Join-Path $repoRoot $project
    if (-not (Test-Path $projectPath)) {
        $failures.Add("Missing project: $project")
        continue
    }

    [xml]$xml = Get-Content -Raw $projectPath
    $actual = @(
        $xml.SelectNodes('//ProjectReference') |
            ForEach-Object {
                $resolved = Resolve-Path -Path (Join-Path (Split-Path $projectPath) $_.Include)
                [System.IO.Path]::GetRelativePath($repoRoot, $resolved).Replace('\', '/')
            }
    )

    $allowed = $rules[$project]
    foreach ($reference in $actual) {
        if ($allowed -notcontains $reference) {
            $failures.Add("$project has forbidden reference to $reference")
        }
    }

    foreach ($reference in $allowed) {
        if ($actual -notcontains $reference) {
            $failures.Add("$project is missing expected reference to $reference")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host 'Project references are valid.'
