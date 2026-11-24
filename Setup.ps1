# One time setup for a fresh clone, run from PowerShell (see README).
# Writes Directory.Build.props.user (gitignored) with the local game, Torch, Magnetar and Quasar paths. The projects
# read the paths from that file; no junctions or other links are made.
param(
    [string]$GameBinaries,
    [string]$TorchBinaries
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
$exitCode = 0
try {
    # A property from the user file, then the committed defaults. Empty values are skipped. Parsed as XML, so a path
    # with &amp; keeps its meaning.
    function Get-PropValue([string]$name) {
        foreach ($file in @('Directory.Build.props.user', 'Directory.Build.props')) {
            if (-not (Test-Path -LiteralPath $file)) { continue }
            try {
                [xml]$document = Get-Content -LiteralPath $file -Raw -ErrorAction Stop
                $node = $document.SelectSingleNode("/Project/PropertyGroup/$name[not(@Condition)]")
                if ($node -and $node.InnerText.Trim()) { return $node.InnerText.Trim() }
            }
            catch {
                Write-Warning "Could not read $file as XML: $($_.Exception.Message)"
            }
        }
        return ''
    }

    function Read-Folder([string]$prompt, [string]$value) {
        $shown = if ($value) { " [$value]" } else { '' }
        $answer = (Read-Host "$prompt$shown").Trim('"', ' ')
        if ([string]::IsNullOrWhiteSpace($answer)) { $answer = $value }
        if ([string]::IsNullOrWhiteSpace($answer)) { throw "No folder given for: $prompt" }
        if (-not (Test-Path -LiteralPath $answer -PathType Container)) { throw "Not a folder: '$answer'" }
        return (Get-Item -LiteralPath $answer).FullName
    }

    function XmlText([string]$value) { return [System.Security.SecurityElement]::Escape($value) }

    if ($GameBinaries) {
        if (-not (Test-Path -LiteralPath $GameBinaries -PathType Container)) { throw "Not a folder: '$GameBinaries'" }
        $GameBinaries = (Get-Item -LiteralPath $GameBinaries).FullName
    }
    if ($TorchBinaries) {
        if (-not (Test-Path -LiteralPath $TorchBinaries -PathType Container)) { throw "Not a folder: '$TorchBinaries'" }
        $TorchBinaries = (Get-Item -LiteralPath $TorchBinaries).FullName
    }

    # Existing values first, so a rerun keeps them as the default answers.
    $game = Get-PropValue 'GameBinaries'
    if (-not $game) { $game = Get-PropValue 'DS64' }
    if (-not $game) { $game = Get-PropValue 'Dedicated64' }
    $torch = Get-PropValue 'TorchBinaries'

    if (-not $GameBinaries) {
        $GameBinaries = Read-Folder 'Folder containing Sandbox.Game.dll (the DedicatedServer64 folder)' $game
    }
    if (-not $TorchBinaries) {
        $TorchBinaries = Read-Folder 'Folder containing Torch.Server.exe' $torch
    }

    # The game binaries are usually the Dedicated Server folder. An explicit Dedicated64 is kept.
    $dedicated = Get-PropValue 'Dedicated64'
    if (-not $dedicated) { $dedicated = $GameBinaries }

    $magnetar = Get-PropValue 'Magnetar'
    $magnetarBin = Get-PropValue 'MagnetarBin'
    if (-not $magnetarBin -and -not $magnetar) {
        # A Magnetar checkout beside this one, as the author keeps it.
        $local = [System.IO.Path]::Combine((Split-Path $PSScriptRoot -Parent), 'magnetar', 'PluginSdk', 'bin', 'Release', 'netstandard2.0')
        if (Test-Path -LiteralPath (Join-Path $local 'PluginSdk.dll')) {
            $magnetarBin = (Get-Item -LiteralPath $local).FullName
        }
    }
    $quasar = Get-PropValue 'Quasar'
    $quasarAbstractions = Get-PropValue 'QuasarPluginAbstractionsAssembly'
    $magnetarProtocol = Get-PropValue 'MagnetarProtocolAssembly'

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('<Project>')
    $lines.Add('  <!-- Local machine paths. Ignored by Git (*.user); Setup.ps1 writes this file. -->')
    $lines.Add('  <PropertyGroup>')
    $lines.Add('    <Dedicated64>' + (XmlText $dedicated) + '</Dedicated64>')
    $lines.Add('    <GameBinaries>' + (XmlText $GameBinaries) + '</GameBinaries>')
    $lines.Add('    <TorchBinaries>' + (XmlText $TorchBinaries) + '</TorchBinaries>')
    if ($magnetar) { $lines.Add('    <Magnetar>' + (XmlText $magnetar) + '</Magnetar>') }
    if ($magnetarBin) { $lines.Add('    <MagnetarBin>' + (XmlText $magnetarBin) + '</MagnetarBin>') }
    if ($quasar) { $lines.Add('    <Quasar>' + (XmlText $quasar) + '</Quasar>') }
    if ($quasarAbstractions) { $lines.Add('    <QuasarPluginAbstractionsAssembly>' + (XmlText $quasarAbstractions) + '</QuasarPluginAbstractionsAssembly>') }
    if ($magnetarProtocol) { $lines.Add('    <MagnetarProtocolAssembly>' + (XmlText $magnetarProtocol) + '</MagnetarProtocolAssembly>') }
    $lines.Add('  </PropertyGroup>')
    $lines.Add('</Project>')

    Set-Content -LiteralPath 'Directory.Build.props.user' -Value $lines -Encoding utf8
    Write-Output 'Wrote Directory.Build.props.user.'
    Write-Output "  Dedicated64:   $dedicated"
    Write-Output "  GameBinaries:  $GameBinaries"
    Write-Output "  TorchBinaries: $TorchBinaries"
    if ($magnetarBin) { Write-Output "  MagnetarBin:   $magnetarBin" }
    Write-Output 'No GameBinaries or TorchBinaries links are used. If an older checkout still has them, remove the links, not the folders they point to.'
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
