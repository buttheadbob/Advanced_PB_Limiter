# One time setup for a fresh clone, run from PowerShell (see README).
#  - Torch plugin: junctions to the Dedicated Server and Torch binaries
#  - Magnetar plugin: Directory.Build.props (local paths), copied from the template
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
$exitCode = 0
try {
    if (-not (Test-Path 'Directory.Build.props')) {
        Copy-Item 'Directory.Build.props.template' 'Directory.Build.props'
        Write-Output 'Created Directory.Build.props.'
        Write-Output 'Edit it if the auto-detected Dedicated Server / Magnetar paths are wrong.'
    }

    $links = [ordered]@{
        GameBinaries  = 'Folder containing SpaceEngineersDedicated.exe (inside Torch: the DedicatedServer64 folder)'
        TorchBinaries = 'Folder containing Torch.Server.exe'
    }

    foreach ($link in $links.Keys) {
        $answer = (Read-Host $links[$link]).Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($answer) -or -not (Test-Path -LiteralPath $answer -PathType Container)) {
            throw "Not a folder: '$answer'. $link was left as it was."
        }
        # A full path, so a relative answer or one with [ ] in it links the folder that was meant.
        $target = (Get-Item -LiteralPath $answer).FullName

        # Only a junction is replaced, never a real folder, and the folder it points to is left alone.
        $existing = Get-Item -LiteralPath $link -Force -ErrorAction SilentlyContinue
        if ($existing) {
            if ($existing.LinkType -ne 'Junction') { throw "$link exists and is not a junction. Move it away first." }
            $existing.Delete()
        }
        # Not through cmd, where & ^ and %NAME% in a folder name change the path. Windows PowerShell reads -Target as a
        # wildcard pattern, so [ ] are escaped there.
        $linkTarget = if ($PSVersionTable.PSVersion.Major -lt 6) { [WildcardPattern]::Escape($target) } else { $target }
        $null = New-Item -ItemType Junction -Path $link -Target $linkTarget
        $made = [string](Get-Item -LiteralPath $link -Force).Target
        if ($made.TrimEnd('\') -ne $target.TrimEnd('\')) {
            throw "The junction $link points to '$made', not '$target'."
        }
        Write-Output "$link -> $target"
    }

    Write-Output 'Done! You can now open "Advanced PB Limiter.sln".'
}
catch {
    Write-Output "An error occurred: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    Pop-Location
}
exit $exitCode
