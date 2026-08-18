<#
.SYNOPSIS
    Verifies a published EMM release the way a user's Dalamud would see it.

.DESCRIPTION
    Fetches the repository manifest over the network, downloads the package it advertises, and
    checks the things Dalamud checks before it will install: that the body deserialises as an
    array, that the entry carries the fields Dalamud requires, and that the InternalName and
    AssemblyVersion inside the zip agree with the entry pointing at it.

    Run after publishing. Everything here reads; nothing is modified.

.PARAMETER RepoUrl
    Repository manifest URL. Defaults to the published one.

.EXAMPLE
    .\tools\verify-release.ps1
#>
[CmdletBinding()]
param(
    [string] $RepoUrl = 'https://raw.githubusercontent.com/local-variable/EMM/main/repo.json'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$failures = New-Object System.Collections.Generic.List[string]
function Check([string] $label, [bool] $ok, [string] $detail = '') {
    if ($ok) {
        Write-Host "  PASS  $label" -ForegroundColor Green
    }
    else {
        Write-Host "  FAIL  $label $detail" -ForegroundColor Red
        $failures.Add("$label $detail")
    }
}

Write-Host "Repository manifest: $RepoUrl"

# --- Fetch and parse -------------------------------------------------------------------------

$response = Invoke-WebRequest -Uri $RepoUrl -UseBasicParsing

# Decode the bytes as UTF-8 rather than trusting Invoke-WebRequest's own decoding, which falls
# back to the system code page when the response carries no charset and would mangle the em
# dashes in the description. This is also what Dalamud does with the body.
$body = $null
if ($response.RawContentStream) {
    $stream = $response.RawContentStream
    $stream.Position = 0
    $bytes = New-Object byte[] $stream.Length
    [void] $stream.Read($bytes, 0, $bytes.Length)
    $body = [System.Text.Encoding]::UTF8.GetString($bytes)
}
elseif ($response.Content -is [byte[]]) {
    $body = [System.Text.Encoding]::UTF8.GetString($response.Content)
}
else {
    $body = [string] $response.Content
}

Check 'manifest fetched' ($response.StatusCode -eq 200) "(HTTP $($response.StatusCode))"

$parsed = $null
try { $parsed = $body | ConvertFrom-Json } catch { }
Check 'manifest is valid JSON' ($null -ne $parsed)
if ($null -eq $parsed) { throw 'Cannot continue: the manifest did not parse.' }

# Dalamud deserialises the body into a List<RemotePluginManifest>, so the top level must be an
# array even when it holds one entry.
Check 'top level is an array' ($body.TrimStart().StartsWith('['))

$entries = @($parsed)
Write-Host "Entries: $($entries.Count)"

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("emm-verify-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    foreach ($entry in $entries) {
        Write-Host ''
        Write-Host "--- $($entry.InternalName) $($entry.AssemblyVersion) ---"

        # Dalamud drops entries missing any of these outright, without a visible error.
        Check 'InternalName present'    (-not [string]::IsNullOrWhiteSpace($entry.InternalName))
        Check 'Name present'            (-not [string]::IsNullOrWhiteSpace($entry.Name))
        Check 'AssemblyVersion present' (-not [string]::IsNullOrWhiteSpace($entry.AssemblyVersion))

        # Required by the build; an entry without them renders as a blank row.
        foreach ($field in 'Author', 'Description', 'Punchline') {
            Check "$field present" (-not [string]::IsNullOrWhiteSpace($entry.$field))
        }

        # API level must be an integer and match what the target Dalamud runs.
        Check 'DalamudApiLevel is an integer' ($entry.DalamudApiLevel -is [int]) "(got '$($entry.DalamudApiLevel)')"

        Check 'DownloadLinkInstall present' (-not [string]::IsNullOrWhiteSpace($entry.DownloadLinkInstall))

        # A testing build activates only when TestingAssemblyVersion exceeds AssemblyVersion.
        # EMM runs no testing channel, so that field must stay absent or the installer will
        # offer a testing build that does not exist.
        Check 'no stray TestingAssemblyVersion' ([string]::IsNullOrWhiteSpace($entry.TestingAssemblyVersion))

        # --- The icon is fetched over HTTP for a third-party install, not read from the zip ---

        if ($entry.IconUrl) {
            $iconOk = $false
            try {
                $icon = Invoke-WebRequest -Uri $entry.IconUrl -UseBasicParsing -Method Head
                $iconOk = ($icon.StatusCode -eq 200)
            }
            catch { }
            Check 'IconUrl resolves' $iconOk "($($entry.IconUrl))"
        }

        # --- The package, and the comparison that blocks the install ------------------------

        $zip = Join-Path $temp "$($entry.InternalName).zip"
        $downloaded = $false
        try {
            Invoke-WebRequest -Uri $entry.DownloadLinkInstall -UseBasicParsing -OutFile $zip
            $downloaded = Test-Path $zip
        }
        catch { }
        Check 'package downloads' $downloaded "($($entry.DownloadLinkInstall))"
        if (-not $downloaded) { continue }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $dll = $archive.Entries | Where-Object { $_.FullName -eq "$($entry.InternalName).dll" }
            Check 'package carries the plugin DLL' ($null -ne $dll)

            $manifestEntry = $archive.Entries | Where-Object { $_.FullName -eq "$($entry.InternalName).json" }
            Check 'package carries its manifest' ($null -ne $manifestEntry)
            if ($null -eq $manifestEntry) { continue }

            $reader = New-Object System.IO.StreamReader($manifestEntry.Open(), [System.Text.Encoding]::UTF8)
            try { $sealed = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }

            # This is the check API 15 added. A mismatch is a hard install failure in game.
            Check 'InternalName matches the package' ($sealed.InternalName -eq $entry.InternalName) `
                "(entry '$($entry.InternalName)' vs package '$($sealed.InternalName)')"
            Check 'AssemblyVersion matches the package' ($sealed.AssemblyVersion -eq $entry.AssemblyVersion) `
                "(entry '$($entry.AssemblyVersion)' vs package '$($sealed.AssemblyVersion)')"
        }
        finally {
            $archive.Dispose()
        }
    }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All checks passed.' -ForegroundColor Green
