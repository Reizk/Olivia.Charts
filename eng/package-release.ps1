param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$releaseDirectory = Join-Path $repositoryRoot "artifacts/releases/v$version"
$demoDirectory = Join-Path $releaseDirectory 'demo-win-x64'
$packageDirectory = Join-Path $repositoryRoot 'artifacts/packages'
$archive = Join-Path $releaseDirectory "Olivia.Charts.Demo-$version-win-x64.zip"
if (Test-Path -LiteralPath $archive) { throw "Release archive already exists: $archive" }

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

Push-Location $repositoryRoot
try {
    Invoke-DotNet restore Olivia.Charts.sln --locked-mode
    Invoke-DotNet build Olivia.Charts.sln -c $Configuration --no-restore
    Invoke-DotNet test Olivia.Charts.sln -c $Configuration --no-build
    Invoke-DotNet publish samples/Olivia.Charts.Demo -c $Configuration -r win-x64 --self-contained false --no-restore -o $demoDirectory
    Invoke-DotNet pack src/Olivia.Charts.Core -c $Configuration --no-build -o $packageDirectory
    Invoke-DotNet pack src/Olivia.Charts.Avalonia -c $Configuration --no-build -o $packageDirectory
    Invoke-DotNet restore eng/package-smoke --configfile eng/package-smoke/NuGet.Config --packages artifacts/package-cache
    Invoke-DotNet run --project eng/package-smoke -c $Configuration --no-restore

    Compress-Archive -Path (Join-Path $demoDirectory '*') -DestinationPath $archive
    $assets = @($archive)
    foreach ($package in @('Olivia.Charts.Core', 'Olivia.Charts.Avalonia')) {
        $file = "$package.$version.nupkg"
        $destination = Join-Path $releaseDirectory $file
        Copy-Item -LiteralPath (Join-Path $packageDirectory $file) -Destination $destination
        $assets += $destination
    }
    $checksums = foreach ($asset in $assets) {
        $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path $asset -Leaf)"
    }
    [IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
    Write-Output "Release assets: $releaseDirectory"
}
finally { Pop-Location }
