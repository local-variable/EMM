<#
.SYNOPSIS
    Generates repo.json, EMM's third-party plugin repository manifest.

.DESCRIPTION
    The manifest is generated from the artifacts DalamudPackager already produced, never
    authored by hand. Dalamud API 15 stopped overwriting the manifest inside the package with
    the repository's copy, and now refuses the install outright when the zip's InternalName or
    AssemblyVersion disagrees with the repository entry. Deriving one from the other is the
    only way that pair cannot drift.

    The script reads the packaged manifest, cross-checks it against the copy sealed inside
    latest.zip, and writes a single-entry JSON array to repo.json at the repository root.

    The download link is pinned to the release tag rather than to /releases/latest/, so an
    entry advertising a version always points at the archive carrying that exact version.

.PARAMETER Configuration
    Build configuration to read. Only Release produces latest.zip.

.PARAMETER Changelog
    Release notes shown in the plugin installer for this version. Optional.

.PARAMETER Tag
    Release tag holding the archive. Defaults to "v" + the packaged AssemblyVersion.

.EXAMPLE
    .\tools\build-repo-json.ps1 -Changelog "First public build."
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Changelog = '',

    [string] $Tag = ''
)

$ErrorActionPreference = 'Stop'

$InternalName = 'EorzeanMarketMaster'
$RepoUrl      = 'https://github.com/local-variable/EMM'

$root        = Split-Path -Parent $PSScriptRoot
$packageDir  = Join-Path $root "$InternalName\bin\x64\$Configuration\$InternalName"
$manifestPth = Join-Path $packageDir "$InternalName.json"
$zipPath     = Join-Path $packageDir 'latest.zip'
$outputPath  = Join-Path $root 'repo.json'

function Read-Utf8Json([string] $path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    return [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
}

# --- The packager must have run -------------------------------------------------------------

if (-not (Test-Path $manifestPth)) {
    throw "No packaged manifest at $manifestPth. Build first: dotnet build $InternalName\$InternalName.csproj -c $Configuration -p:Platform=x64"
}
if (-not (Test-Path $zipPath)) {
    throw "No package at $zipPath. Only a Release build produces latest.zip."
}

$manifest = Read-Utf8Json $manifestPth

# Omitting -p:Platform=x64 does not fail the build, it just packages to bin/<Config>/ instead.
# The package here can therefore be a leftover from an earlier, correct build while the source
# has moved on. Compare against the newest input rather than trusting that it is current.
$projectDir = Join-Path $root $InternalName
$newestInput = Get-ChildItem $projectDir -Recurse -Include '*.cs', '*.csproj' -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

$package = Get-Item $zipPath
if ($newestInput -and $newestInput.LastWriteTimeUtc -gt $package.LastWriteTimeUtc) {
    throw ("Package is stale: $($newestInput.Name) changed at $($newestInput.LastWriteTimeUtc.ToString('u')) " +
           "but latest.zip was built at $($package.LastWriteTimeUtc.ToString('u')). " +
           "Rebuild with -c $Configuration -p:Platform=x64.")
}

# --- Cross-check against the manifest sealed inside the zip ---------------------------------
# This is the pair Dalamud compares on install. If they disagree here, they disagree in game.

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entry = $archive.Entries | Where-Object { $_.FullName -eq "$InternalName.json" }
    if (-not $entry) { throw "latest.zip carries no $InternalName.json." }

    $reader = New-Object System.IO.StreamReader($entry.Open(), [System.Text.Encoding]::UTF8)
    try { $sealed = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
}
finally {
    $archive.Dispose()
}

if ($sealed.InternalName -ne $manifest.InternalName) {
    throw "InternalName disagrees: package '$($sealed.InternalName)' vs manifest '$($manifest.InternalName)'."
}
if ($sealed.AssemblyVersion -ne $manifest.AssemblyVersion) {
    throw "AssemblyVersion disagrees: package '$($sealed.AssemblyVersion)' vs manifest '$($manifest.AssemblyVersion)'."
}
if ($manifest.InternalName -ne $InternalName) {
    throw "InternalName is '$($manifest.InternalName)', expected '$InternalName'. InternalName is permanent; this should never change."
}
if (-not $manifest.AssemblyVersion) {
    throw 'AssemblyVersion is empty. Dalamud drops entries with a null AssemblyVersion.'
}

# --- Build the entry ------------------------------------------------------------------------

if (-not $Tag) { $Tag = 'v' + $manifest.AssemblyVersion }
$downloadUrl = "$RepoUrl/releases/download/$Tag/latest.zip"

# Every manifest field is a legal key in a repository entry; these are the repository-only ones.
# DownloadLinkUpdate has no reader in Dalamud but is set alongside Install, as the documented
# sample and the live third-party repositories both do.
# No testing channel exists: DownloadLinkTesting is present for schema completeness, and with
# TestingAssemblyVersion absent a testing build can never activate.
$entryOut = [ordered] @{}
foreach ($property in $manifest.PSObject.Properties) {
    $entryOut[$property.Name] = $property.Value
}
$entryOut['DownloadLinkInstall'] = $downloadUrl
$entryOut['DownloadLinkUpdate']  = $downloadUrl
$entryOut['DownloadLinkTesting'] = $downloadUrl
$entryOut['IsHide']              = $false
$entryOut['DownloadCount']       = 0
$entryOut['LastUpdate']          = [datetimeoffset]::UtcNow.ToUnixTimeSeconds()
if ($Changelog) { $entryOut['Changelog'] = $Changelog }

$json = ConvertTo-Json @([pscustomobject] $entryOut) -Depth 10

# A repository body must deserialise as a List<RemotePluginManifest>, so a single entry still
# has to be wrapped in an array. ConvertTo-Json unwraps a one-element array, so force it back.
if (-not $json.TrimStart().StartsWith('[')) { $json = "[$([Environment]::NewLine)$json$([Environment]::NewLine)]" }

$json = $json -replace "`r`n", "`n"
if (-not $json.EndsWith("`n")) { $json += "`n" }

# UTF-8 without a BOM, LF endings. Written through the byte API rather than Set-Content, which
# defaults to the system code page in Windows PowerShell and would mangle the em dashes the
# description carries.
[System.IO.File]::WriteAllBytes($outputPath, [System.Text.Encoding]::UTF8.GetBytes($json))

# --- Report ---------------------------------------------------------------------------------

Write-Host "Wrote $outputPath"
Write-Host "  InternalName    $($manifest.InternalName)"
Write-Host "  AssemblyVersion $($manifest.AssemblyVersion)  (matches latest.zip)"
Write-Host "  DalamudApiLevel $($manifest.DalamudApiLevel)"
Write-Host "  Download        $downloadUrl"
Write-Host ''
Write-Host "The download link 404s until the release exists. Publish it with:"
Write-Host "  gh release create $Tag `"$zipPath`" --title $Tag --notes `"...`""
