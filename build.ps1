# Builds Advanced PB Limiter for Torch, Magnetar and the Quasar plugin.
#
#   .\build.ps1                              all of them, Release
#   .\build.ps1 torch                        Torch only      -> Build\Advanced PB Limiter.zip
#   .\build.ps1 magnetar                     Magnetar only   -> ServerPlugin\bin\<config>\net48 and net10.0
#   .\build.ps1 quasar                       Quasar plugin and its companion only (needs a quasar checkout in ..\quasar,
#                                            or -QuasarAbstractions pointing at a Quasar 1.7 Quasar.Plugin.Abstractions.dll)
#   .\build.ps1 all debug                    all of them, Debug
#   .\build.ps1 magnetar release nodeploy    do not copy into %AppData%\Magnetar\...\Local
#
# Torch needs the .NET Framework MSBuild (Visual Studio), so it builds on Windows only. Magnetar and Quasar
# need the .NET SDK (dotnet) and build on Windows, Linux and macOS, from Windows PowerShell or PowerShell 7
# (pwsh ./build.ps1 magnetar).
param(
    [string]$Target = 'all',
    [string]$Configuration = 'Release',
    [string]$Deploy = '',
    [string]$QuasarAbstractions = $env:QUASAR_PLUGIN_ABSTRACTIONS_ASSEMBLY,
    [string]$MagnetarProtocol = $env:MAGNETAR_PROTOCOL_ASSEMBLY
)

if ($Target -notin 'all', 'torch', 'magnetar', 'quasar') {
    Write-Output "Unknown target ""$Target"". Use: torch, magnetar, quasar or all."
    exit 2
}

# Reads a local path property from the user file, then the committed defaults. Empty when unset.
function Get-BuildProp([string]$name) {
    foreach ($file in @('Directory.Build.props.user', 'Directory.Build.props')) {
        if (-not (Test-Path -LiteralPath $file)) { continue }
        try {
            [xml]$document = Get-Content -LiteralPath $file -Raw -ErrorAction Stop
            $node = $document.SelectSingleNode("/Project/PropertyGroup/$name[not(@Condition)]")
            if ($node -and $node.InnerText.Trim()) { return $node.InnerText.Trim() }
        }
        catch {
            # A malformed props file is reported by the build itself; here it only means no value was found.
        }
    }
    return ''
}

# The caller's shell goes back to its own folder afterwards, as the old build.bat did with setlocal.
Push-Location $PSScriptRoot
try {
    $deployToMagnetar = if ($Deploy -eq 'nodeploy') { 'false' } else { 'true' }
    $failed = $false
    $onWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

    # What stops each requested build is worked out before the version step: a run that can build nothing it was asked
    # for never raises the version. Off Windows, Torch is only skipped when everything was asked for.
    $torchProblem = $null
    $msbuild = $null
    if ($Target -in 'all', 'torch') {
        if (-not $onWindows) {
            $torchProblem = 'Skipped: Torch builds only on Windows, with Visual Studio MSBuild.'
        }
        else {
            $gameBinaries = Get-BuildProp 'GameBinaries'
            if (-not $gameBinaries) { $gameBinaries = Get-BuildProp 'DS64' }
            if (-not $gameBinaries) { $gameBinaries = Get-BuildProp 'Dedicated64' }

            $torchBinaries = Get-BuildProp 'TorchBinaries'

            if (-not $gameBinaries -or -not (Test-Path -LiteralPath (Join-Path $gameBinaries 'Sandbox.Game.dll'))) {
                $torchProblem = 'ERROR: GameBinaries (or DS64/Dedicated64) is missing or has no Sandbox.Game.dll. Run Setup.ps1 or set it in Directory.Build.props.user.'
            }
            elseif (-not $torchBinaries -or -not (Test-Path -LiteralPath (Join-Path $torchBinaries 'Torch.dll'))) {
                $torchProblem = 'ERROR: TorchBinaries is missing or has no Torch.dll. Run Setup.ps1 or set it in Directory.Build.props.user.'
            }
            else {
                $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
                if (Test-Path $vswhere) {
                    $vswhereArgs = @(
                        '-latest', '-prerelease', '-requires', 'Microsoft.Component.MSBuild',
                        '-find', 'MSBuild\**\Bin\MSBuild.exe'
                    )
                    $msbuild = & $vswhere @vswhereArgs | Select-Object -First 1
                }
                if (-not $msbuild) {
                    $torchProblem = 'ERROR: Visual Studio MSBuild not found. Install the .NET desktop workload.'
                }
            }
        }
    }
    $magnetarProblem = $null
    $magnetarBin = Get-BuildProp 'MagnetarBin'
    if (-not $magnetarBin) {
        # The main props derive MagnetarBin from Magnetar; do the same here so a missing install fails before the
        # version is raised, as a missing Directory.Build.props used to.
        $magnetar = Get-BuildProp 'Magnetar'
        if (-not $magnetar -and $onWindows -and $env:APPDATA) { $magnetar = Join-Path $env:APPDATA 'Magnetar' }
        if (-not $magnetar -and -not $onWindows -and $HOME) { $magnetar = Join-Path $HOME '.local/share/Magnetar' }
        if ($magnetar) {
            foreach ($file in @(
                [System.IO.Path]::Combine($magnetar, 'Libraries', 'MagnetarLegacy', 'PluginSdk.dll'),
                [System.IO.Path]::Combine($magnetar, 'Libraries', 'MagnetarInterim', 'PluginSdk.dll'),
                [System.IO.Path]::Combine($magnetar, 'Bin', 'PluginSdk.dll')
            )) {
                if (Test-Path -LiteralPath $file) { $magnetarBin = Split-Path $file -Parent; break }
            }
        }
        if (-not $magnetarBin) {
            $local = [System.IO.Path]::Combine((Split-Path $PSScriptRoot -Parent), 'magnetar', 'PluginSdk', 'bin', 'Release', 'netstandard2.0')
            if (Test-Path -LiteralPath (Join-Path $local 'PluginSdk.dll')) { $magnetarBin = $local }
        }
    }
    if ($magnetarBin -and -not (Test-Path -LiteralPath (Join-Path $magnetarBin 'PluginSdk.dll'))) {
        $magnetarProblem = "ERROR: PluginSdk.dll not found in MagnetarBin: $magnetarBin. Fix Directory.Build.props.user."
    }
    elseif (-not $magnetarBin) {
        $magnetarProblem = 'ERROR: Magnetar PluginSdk not found. Run Setup.ps1 or set MagnetarBin in Directory.Build.props.user.'
    }
    $quasarProblem = $null
    $quasarAbstractionsArg = $null
    if (-not $QuasarAbstractions) { $QuasarAbstractions = Get-BuildProp 'QuasarPluginAbstractionsAssembly' }
    if (-not $MagnetarProtocol) { $MagnetarProtocol = Get-BuildProp 'MagnetarProtocolAssembly' }
    if ($QuasarAbstractions) {
        if (Test-Path $QuasarAbstractions) {
            $quasarAbstractionsArg = "-p:QuasarPluginAbstractionsAssembly=$QuasarAbstractions"
        }
        else {
            $quasarProblem = "ERROR: QuasarPluginAbstractionsAssembly not found: $QuasarAbstractions"
        }
    }
    elseif (-not (Test-Path '../quasar/Quasar.Plugin.Abstractions')) {
        $quasarProblem = 'Skipped: needs a quasar checkout next to this folder (../quasar) or -QuasarAbstractions.'
    }
    $canTorch = $Target -in 'all', 'torch' -and -not $torchProblem
    $canMagnetar = $Target -in 'all', 'magnetar' -and -not $magnetarProblem
    $canQuasar = $Target -in 'all', 'quasar' -and -not $quasarProblem
    $canBuild = $canTorch -or $canMagnetar -or $canQuasar

    if ($canBuild) {
        Write-Output ''
        Write-Output '===== Version ====='
        # In this same PowerShell, so it works however PowerShell is installed. Its exit only ends that script and
        # sets the exit code, and its StrictMode and error preference stay in its own scope.
        & (Join-Path $PSScriptRoot 'Versioning/version.ps1')
        if ($LASTEXITCODE -ne 0) {
            Write-Output 'ERROR: Versioning/version.ps1 failed.'
            $failed = $true
            $Target = 'none' # Nothing is built with a version that was not raised.
        }
    }
    else {
        Write-Output ''
        Write-Output 'Nothing that was asked for can be built here, so the version was not raised:'
    }

    if ($Target -in 'all', 'torch') {
        Write-Output ''
        Write-Output "===== Torch ($Configuration) ====="
        if ($torchProblem) {
            Write-Output $torchProblem
            # Torch only being skipped off Windows does not fail a build of everything.
            if ($onWindows -or $Target -eq 'torch') { $failed = $true }
        }
        else {
            $torchArgs = @(
                'TorchPlugin/Advanced PB Limiter.csproj', "-p:Configuration=$Configuration",
                '-p:Platform=x64', '-v:m', '-nologo'
            )
            & $msbuild @torchArgs
            if ($LASTEXITCODE -ne 0) {
                Write-Output 'Torch build FAILED.'
                $failed = $true
            }
            else {
                Write-Output "Torch build OK: $(Join-Path $PSScriptRoot 'Build/Advanced PB Limiter.zip')"
            }
        }
    }

    if ($Target -in 'all', 'magnetar') {
        Write-Output ''
        Write-Output "===== Magnetar ($Configuration) ====="
        if ($magnetarProblem) {
            Write-Output $magnetarProblem
            $failed = $true
        }
        else {
            $magnetarArgs = @(
                'ServerPlugin/ServerPlugin.csproj', '-c', $Configuration,
                "-p:DeployToMagnetar=$deployToMagnetar", '-v:m', '-nologo'
            )
            & dotnet build @magnetarArgs
            if ($LASTEXITCODE -ne 0) {
                Write-Output 'Magnetar build FAILED.'
                $failed = $true
            }
            else {
                Write-Output "Magnetar build OK: $(Join-Path $PSScriptRoot "ServerPlugin/bin/$Configuration")"
            }
        }
    }

    if ($Target -in 'all', 'quasar') {
        Write-Output ''
        Write-Output "===== Quasar plugin ($Configuration) ====="
        # Quasar builds the plugin itself when it is installed from QuasarHub.
        # Local builds compile against a quasar checkout.
        if ($quasarProblem) {
            Write-Output $quasarProblem
            if ($Target -eq 'quasar') { $failed = $true }
        }
        else {
            $plugin = 'QuasarPlugin/AdvancedPBLimiter.Quasar/AdvancedPBLimiter.Quasar.csproj'
            $companion = 'QuasarPlugin/AdvancedPBLimiter.QuasarCompanion/AdvancedPBLimiter.QuasarCompanion.csproj'
            $pluginArgs = @($plugin, '-c', $Configuration, '-v:m', '-nologo')
            if ($quasarAbstractionsArg) { $pluginArgs += $quasarAbstractionsArg }
            & dotnet build @pluginArgs
            if ($LASTEXITCODE -ne 0) {
                Write-Output 'Quasar plugin build FAILED.'
                $failed = $true
            }
            else {
                $companionArgs = @($companion, '-c', $Configuration, '-p:Platform=x64', '-v:m', '-nologo')
                if ($MagnetarProtocol -and (Test-Path $MagnetarProtocol)) {
                    $companionArgs += "-p:MagnetarProtocolAssembly=$MagnetarProtocol"
                }
                & dotnet build @companionArgs
                if ($LASTEXITCODE -ne 0) {
                    Write-Output 'Quasar companion build FAILED.'
                    $failed = $true
                }
                else {
                    Write-Output "Quasar plugin build OK: $(Join-Path $PSScriptRoot 'QuasarPlugin')"
                }
            }
        }
    }
}
catch {
    # A tool that is not installed, dotnet say, stops the script here instead of each step checking for it.
    Write-Output "ERROR: $($_.Exception.Message)"
    $failed = $true
}
finally {
    Pop-Location
}

Write-Output ''
if ($failed) {
    Write-Output '===== BUILD FAILED ====='
    exit 1
}
Write-Output '===== BUILD OK ====='
exit 0
