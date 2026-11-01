param(
    [Parameter(Mandatory = $true)][string]$CatalogPath,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$backend = Split-Path -Parent $PSScriptRoot
$catalogFile = (Resolve-Path -LiteralPath $CatalogPath).Path
$catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
if (@($catalog.Restaurant.Locations | Where-Object { $null -ne $_ }).Count -eq 0 -or
    @($catalog.Restaurant.Tables | Where-Object { $null -ne $_ }).Count -eq 0 -or
    @($catalog.Restaurant.Menu | Where-Object { $null -ne $_ }).Count -eq 0) {
    throw 'The explicitly supplied catalog must contain restaurant locations, tables, and menu items.'
}
if (Test-Path -LiteralPath $PublishDirectory) { throw 'Use a new, empty publish directory.' }
if (Test-Path -LiteralPath $ZipPath) { throw 'Use a new output ZIP path.' }
& $DotnetPath publish (Join-Path $backend 'Restaurant.Api/Restaurant.Api.csproj') -c Release -r linux-arm64 --self-contained false -p:PublishReadyToRun=false -o $PublishDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item -LiteralPath $catalogFile -Destination (Join-Path $PublishDirectory 'appsettings.Production.json')
Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $ZipPath -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
try {
    if ($null -eq $archive.GetEntry('appsettings.Production.json') -or $null -eq $archive.GetEntry('Restaurant.Api.dll')) {
        throw 'Package validation failed: API or catalog is missing.'
    }
} finally { $archive.Dispose() }
Write-Output 'API package built and catalog presence verified.'
