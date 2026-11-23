# Run by build.ps1 before every build. Increments the build number (fourth part) of the version in
# AssemblyVersion.cs, which the Torch, Magnetar and Quasar projects compile, and copies it into the Torch manifest
# and the Quasar plugin manifest (quasar-plugin.json).
# To start a new release, edit the first three parts in AssemblyVersion.cs by hand (for example 1.4.0.0).
# Every file is read and its new text worked out before any is written, and any failure ends with exit code 1,
# so a build never runs with versions that disagree.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

try {
    $versionFile = "$PSScriptRoot/AssemblyVersion.cs"
    $manifestPath = "$PSScriptRoot/../TorchPlugin/manifest.xml"
    $quasarManifestPath = "$PSScriptRoot/../quasar-plugin.json"
    $versionPattern = 'AssemblyVersion\("([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)"\)'

    $match = [regex]::Match([System.IO.File]::ReadAllText($versionFile), $versionPattern)
    if (-not $match.Success) { throw "Could not read the version from $versionFile" }
    $major = [int]$match.Groups[1].Value
    $minor = [int]$match.Groups[2].Value
    $patch = [int]$match.Groups[3].Value
    $build = [int]$match.Groups[4].Value + 1

    $dotNetVersion = "$major.$minor.$patch.$build"
    $infoVersion = "v$dotNetVersion"

    $versionText = @"
using System.Reflection;

[assembly: AssemblyVersion("$dotNetVersion")]
[assembly: AssemblyFileVersion("$dotNetVersion")]
[assembly: AssemblyInformationalVersion("$infoVersion")]

"@

    $manifest = [System.IO.File]::ReadAllText($manifestPath)
    if ($manifest -notmatch '<Version>.*?</Version>') { throw "No <Version> in $manifestPath" }
    $manifest = $manifest -replace '<Version>.*?</Version>', "<Version>$infoVersion</Version>"

    $quasarManifest = [System.IO.File]::ReadAllText($quasarManifestPath)
    if ($quasarManifest -cnotmatch '"version": "[^"]*"') { throw "No version in $quasarManifestPath" }
    $quasarManifest = $quasarManifest -creplace '"version": "[^"]*"', "`"version`": `"$dotNetVersion`""

    [System.IO.File]::WriteAllText($versionFile, $versionText)
    [System.IO.File]::WriteAllText($manifestPath, $manifest)
    [System.IO.File]::WriteAllText($quasarManifestPath, $quasarManifest)

    Write-Output $infoVersion
}
catch {
    Write-Output "ERROR: $($_.Exception.Message)"
    exit 1
}
# Said outright, so a caller running this in its own PowerShell never reads an exit code left by an earlier command.
exit 0
