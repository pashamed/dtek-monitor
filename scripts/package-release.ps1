param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Tag,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\releases')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = $Tag.Substring(1)
$manifest = Get-Content (Join-Path $root '.release-please-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.'.' -ne $version) {
    throw 'The requested release tag does not match the checked-out release manifest.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path $OutputDirectory "publish-$version"
$archive = Join-Path $OutputDirectory "dtek-bot-$Tag-iis.zip"
if ((Test-Path -LiteralPath $publish) -or (Test-Path -LiteralPath $archive)) {
    throw 'Release output already exists. Choose a new output directory.'
}

Push-Location (Join-Path $root 'bot')
try {
    dotnet publish src\DtekBot.Api\DtekBot.Api.csproj --configuration Release --output $publish -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
}
finally {
    Pop-Location
}

# Production settings stay on the server; environment-specific settings are never shipped.
Get-ChildItem -LiteralPath $publish -File -Filter 'appsettings.*.json' | Remove-Item
foreach ($required in @('DtekBot.Api.dll', 'web.config', 'dtek-settings.json', 'wwwroot\lite\index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) {
        throw "The IIS package is missing $required."
    }
}
$builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'DtekBot.Api.dll')).ProductVersion.Split('+')[0]
if ($builtVersion -ne $version) {
    throw "Built API version $builtVersion does not match $Tag."
}
$forbidden = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
    $_.Name -like '.env*' -or $_.Extension -in @('.pubxml', '.user', '.log', '.pfx', '.key') -or
    $_.FullName -match '[\\/](logs|artifacts|PublishProfiles)[\\/]'
}
if ($forbidden) { throw 'Unexpected private/runtime files found in the publish output.' }

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archive))" | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Created $archive and SHA256SUMS.txt"
