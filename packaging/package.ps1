$ErrorActionPreference = 'Stop'

$packagingRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $packagingRoot
$modRoot = Join-Path $repoRoot 'src'
$bindingsPath = Join-Path $modRoot 'Bindings.cs'
$projectPath = Join-Path $modRoot 'GuildrunTargetingMod.csproj'
$builtDllPath = Join-Path $modRoot 'bin\Release\GuildrunTargetingMod.dll'
$cacheDirectory = Join-Path $packagingRoot 'cache'
$melonLoaderZip = Join-Path $cacheDirectory 'MelonLoader.x64.v0.7.3.zip'
$melonLoaderUrl = 'https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip'
$expectedMelonLoaderHash = '5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416'
$stagePath = Join-Path $packagingRoot 'stage'
$modOnlyStagePath = Join-Path $packagingRoot 'stage-mod-only'
$readmeTemplatePath = Join-Path $packagingRoot 'README.txt'
$modOnlyReadmeTemplatePath = Join-Path $packagingRoot 'README-mod-only.txt'
$distDirectory = Join-Path $packagingRoot 'dist'

# Typography gate, for the readmes a player reads and for the source anyone reads on GitHub.
# Straight ASCII punctuation only, and no decorative symbols. Accented letters are deliberately
# absent from this list : the French player text needs them.
#
# The rule was a promise before it was a check, and a promise is not a gate. Failing the build
# costs nothing next to noticing an em dash after the archive is published.
$BannedCharacters = [ordered]@{
    'em dash'                   = [char]0x2014
    'en dash'                   = [char]0x2013
    'curly opening quote'       = [char]0x2018
    'curly closing quote'       = [char]0x2019
    'curly opening doublequote' = [char]0x201C
    'curly closing doublequote' = [char]0x201D
    'star'                      = [char]0x2605
    'warning sign'              = [char]0x26A0
    'rightwards arrow'          = [char]0x2192
    'left right arrow'          = [char]0x2194
    'box drawing dash'          = [char]0x2500
    'middle dot'                = [char]0x00B7
    'section sign'              = [char]0x00A7
    'multiplication sign'       = [char]0x00D7
}

function Assert-CleanText {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$Source
    )

    foreach ($name in $BannedCharacters.Keys) {
        if ($Text.IndexOf($BannedCharacters[$name]) -ge 0) {
            throw "Banned character ($name) found in: $Source"
        }
    }
}

function Assert-CleanTree {
    $files = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    foreach ($directory in @((Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'docs'))) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            continue
        }
        Get-ChildItem -LiteralPath $directory -Recurse -File |
            Where-Object { $_.Extension -in '.cs', '.md' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $files.Add($_) }
    }
    foreach ($name in @('README.md', 'CHANGELOG.md')) {
        $path = Join-Path $repoRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $files.Add((Get-Item -LiteralPath $path))
        }
    }

    foreach ($file in $files) {
        Assert-CleanText -Text ([System.IO.File]::ReadAllText($file.FullName)) -Source $file.FullName
    }
    Write-Output "Typography gate: $($files.Count) source and documentation files clean."
}

function New-ReleaseZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$ZipPath,

        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedTopLevelEntries
    )

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    $entryNames = New-Object System.Collections.Generic.List[string]
    $zipArchive = $null
    try {
        $zipArchive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        $sourceFiles = Get-ChildItem -LiteralPath $SourceDirectory -File -Recurse
        foreach ($file in $sourceFiles) {
            $relativePath = $file.FullName.Substring($SourceDirectory.Length).TrimStart([char[]]@('\', '/'))
            $entryName = $relativePath.Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zipArchive,
                $file.FullName,
                $entryName
            ) | Out-Null
            $entryNames.Add($entryName)
        }
    }
    finally {
        if ($null -ne $zipArchive) {
            $zipArchive.Dispose()
        }
    }

    $topLevelEntries = @(
        $entryNames |
            ForEach-Object { ($_ -split '/', 2)[0] } |
            Sort-Object -Unique
    )
    $zipFileInfo = Get-Item -LiteralPath $ZipPath
    $zipSizeMb = $zipFileInfo.Length / 1MB
    $zipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToUpperInvariant()

    Write-Output ''
    Write-Output "Archive report: $($zipFileInfo.Name)"
    Write-Output "Zip path: $($zipFileInfo.FullName)"
    Write-Output "Entry count: $($entryNames.Count)"
    Write-Output ("Zip size (MB): {0:F2}" -f $zipSizeMb)
    Write-Output "Zip SHA-256: $zipHash"
    Write-Output 'Top-level entries:'
    foreach ($topLevelEntry in $topLevelEntries) {
        Write-Output $topLevelEntry
    }

    $topLevelDifference = @(Compare-Object -ReferenceObject $ExpectedTopLevelEntries -DifferenceObject $topLevelEntries)
    if ($topLevelDifference.Count -ne 0) {
        throw "Zip top-level entry set does not match the expected set: $ZipPath"
    }
}

if (-not (Test-Path -LiteralPath $bindingsPath -PathType Leaf)) {
    throw "Bindings file not found: $bindingsPath"
}

$bindingsContent = [System.IO.File]::ReadAllText($bindingsPath)
$versionMatch = [System.Text.RegularExpressions.Regex]::Match($bindingsContent, 'ModVersion\s*=\s*"([^"]+)"')
if (-not $versionMatch.Success) {
    throw "Could not extract ModVersion from: $bindingsPath"
}
$modVersion = $versionMatch.Groups[1].Value

if (-not (Test-Path -LiteralPath $melonLoaderZip -PathType Leaf)) {
    if (-not (Test-Path -LiteralPath $cacheDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $cacheDirectory | Out-Null
    }
    Invoke-WebRequest -Uri $melonLoaderUrl -OutFile $melonLoaderZip
}

$actualMelonLoaderHash = (Get-FileHash -LiteralPath $melonLoaderZip -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualMelonLoaderHash -ne $expectedMelonLoaderHash) {
    Write-Output "Expected MelonLoader SHA-256: $expectedMelonLoaderHash"
    Write-Output "Actual MelonLoader SHA-256:   $actualMelonLoaderHash"
    throw "MelonLoader cache SHA-256 mismatch: $melonLoaderZip"
}

Assert-CleanTree

& dotnet build $projectPath -c Release
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $builtDllPath -PathType Leaf)) {
    throw "Built mod DLL not found: $builtDllPath"
}

if (Test-Path -LiteralPath $stagePath) {
    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
New-Item -ItemType Directory -Path $stagePath | Out-Null
Expand-Archive -LiteralPath $melonLoaderZip -DestinationPath $stagePath

$requiredStageItems = @(
    @{ Path = (Join-Path $stagePath 'version.dll'); Type = 'Leaf' },
    @{ Path = (Join-Path $stagePath 'MelonLoader'); Type = 'Container' },
    @{ Path = (Join-Path $stagePath 'MelonLoader\Documentation\LICENSE.md'); Type = 'Leaf' },
    @{ Path = (Join-Path $stagePath 'MelonLoader\Documentation\NOTICE.txt'); Type = 'Leaf' }
)
foreach ($requiredItem in $requiredStageItems) {
    if (-not (Test-Path -LiteralPath $requiredItem.Path -PathType $requiredItem.Type)) {
        throw "Required staged MelonLoader item missing: $($requiredItem.Path)"
    }
}

$modsDirectory = Join-Path $stagePath 'Mods'
New-Item -ItemType Directory -Path $modsDirectory | Out-Null
Copy-Item -LiteralPath $builtDllPath -Destination (Join-Path $modsDirectory 'GuildrunTargetingMod.dll')

if (-not (Test-Path -LiteralPath $readmeTemplatePath -PathType Leaf)) {
    throw "README template not found: $readmeTemplatePath"
}
$readmeTemplate = [System.IO.File]::ReadAllText($readmeTemplatePath)
if (-not $readmeTemplate.Contains('{VERSION}')) {
    throw "README template does not contain the literal token {VERSION}: $readmeTemplatePath"
}
$stagedReadme = $readmeTemplate.Replace('{VERSION}', $modVersion)
Assert-CleanText -Text $stagedReadme -Source $readmeTemplatePath
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $stagePath 'README.txt'), $stagedReadme, $utf8WithoutBom)

if (Test-Path -LiteralPath $modOnlyStagePath) {
    Remove-Item -LiteralPath $modOnlyStagePath -Recurse -Force
}
New-Item -ItemType Directory -Path $modOnlyStagePath | Out-Null

$modOnlyModsDirectory = Join-Path $modOnlyStagePath 'Mods'
New-Item -ItemType Directory -Path $modOnlyModsDirectory | Out-Null
Copy-Item -LiteralPath $builtDllPath -Destination (Join-Path $modOnlyModsDirectory 'GuildrunTargetingMod.dll')

if (-not (Test-Path -LiteralPath $modOnlyReadmeTemplatePath -PathType Leaf)) {
    throw "Mod-only README template not found: $modOnlyReadmeTemplatePath"
}
$modOnlyReadmeTemplate = [System.IO.File]::ReadAllText($modOnlyReadmeTemplatePath)
if (-not $modOnlyReadmeTemplate.Contains('{VERSION}')) {
    throw "Mod-only README template does not contain the literal token {VERSION}: $modOnlyReadmeTemplatePath"
}
$modOnlyStagedReadme = $modOnlyReadmeTemplate.Replace('{VERSION}', $modVersion)
Assert-CleanText -Text $modOnlyStagedReadme -Source $modOnlyReadmeTemplatePath
[System.IO.File]::WriteAllText((Join-Path $modOnlyStagePath 'README.txt'), $modOnlyStagedReadme, $utf8WithoutBom)

if (-not (Test-Path -LiteralPath $distDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $distDirectory | Out-Null
}
$fullZipPath = Join-Path $distDirectory ("GuildrunTargetingMod-v{0}-with-MelonLoader.zip" -f $modVersion)
$modOnlyZipPath = Join-Path $distDirectory ("GuildrunTargetingMod-v{0}-mod-only.zip" -f $modVersion)

# Download-link gate. README.md carries one-click links straight to the release assets, because
# most players have never downloaded anything from GitHub and will not find the Assets list on
# their own. Those links name a version, so they rot the moment one ships without them being
# updated, and a rotted link is invisible here : it still resolves, it just hands out the old
# build forever. Same reasoning as the typography gate above, so the same answer : fail the build
# rather than trust anyone to remember.
function Assert-ReadmeDownloadLinks {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$MelonLoaderUrl
    )

    $readmePath = Join-Path $repoRoot 'README.md'
    $text = [System.IO.File]::ReadAllText($readmePath)
    $expected = @(
        "GuildrunTargetingMod-v$Version-with-MelonLoader.zip",
        "GuildrunTargetingMod-v$Version-mod-only.zip"
    )

    # The blocked-download fallback tells players to fetch MelonLoader themselves. That link has to
    # be the same one this script downloads and hash-checks above, or the fallback quietly hands
    # them a different loader than the one in the full zip. $melonLoaderUrl is the single home for
    # that fact ; this only asserts the readme still agrees with it.
    $melonLinks = [regex]::Matches($text, 'https://github\.com/LavaGang/MelonLoader/releases/download/[^\s)]+') |
                  ForEach-Object { $_.Value } | Sort-Object -Unique
    if (-not $melonLinks) {
        throw "README.md never links MelonLoader directly. The blocked-download fallback needs it: $readmePath"
    }
    foreach ($link in $melonLinks) {
        if ($link -ne $MelonLoaderUrl) {
            throw "README.md links MelonLoader at '$link', but this build ships '$MelonLoaderUrl'. Update: $readmePath"
        }
    }

    # Every release-asset link in the readme, whatever version it names. Scoped to this repo on
    # purpose : the readme also links MelonLoader's own release asset, which is checked separately
    # below and must not be measured against what this build produces.
    $found = [regex]::Matches($text, 'https://github\.com/Larsonix/GuildrunTargetingMod/releases/download/v[^/\s)]+/([^\s)]+\.zip)') |
             ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    if (-not $found) {
        throw "README.md has no release-asset download links. They are the download route for players : $readmePath"
    }
    foreach ($name in $found) {
        if ($expected -notcontains $name) {
            throw "README.md download link points at '$name', which this build does not produce (version $Version). Update the links in: $readmePath"
        }
    }
    foreach ($name in $expected) {
        if ($found -notcontains $name) {
            throw "README.md is missing a download link for '$name'. Add it to: $readmePath"
        }
    }
    Write-Output "Download-link gate: README.md links match version $Version and the shipped MelonLoader."
}
Assert-ReadmeDownloadLinks -Version $modVersion -MelonLoaderUrl $melonLoaderUrl

Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Output "Mod version: $modVersion"
New-ReleaseZip -SourceDirectory $stagePath -ZipPath $fullZipPath -ExpectedTopLevelEntries @('MelonLoader', 'Mods', 'README.txt', 'version.dll')
New-ReleaseZip -SourceDirectory $modOnlyStagePath -ZipPath $modOnlyZipPath -ExpectedTopLevelEntries @('Mods', 'README.txt')
