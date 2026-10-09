<#
.SYNOPSIS
Installs MelonLoader 0.7.3 and the GH3 gameplay skin into Windows Clone Hero v1.1.0.6142-final.

.DESCRIPTION
Run this script from the release folder (the folder with Mods\Gh3Skin.dll). It checks the game version, installs
MelonLoader 0.7.3 x64 if it is not installed, and copies the mod, the shader bundles, the HUD assets, and the stage and
band packages into the Mods folder of the game. See docs/PLAYER-GUIDE.md, section "Install on Windows".

.PARAMETER GameDir
The Clone Hero folder (the folder with Clone Hero.exe). Without it, the script uses the current folder if it has
Clone Hero.exe, or asks for the folder.

.PARAMETER MelonLoaderZip
A local MelonLoader.x64.zip of version 0.7.3. Without it, the script downloads the file from GitHub.

.EXAMPLE
.\install-windows.ps1 -GameDir "C:\Games\Clone Hero"
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [string]$MelonLoaderZip = ""
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$gameVersion = 'v1.1.0.6142-final'
$melonVersion = '0.7.3'
$melonUrl = "https://github.com/LavaGang/MelonLoader/releases/download/v$melonVersion/MelonLoader.x64.zip"
$melonSha256 = '5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416'
$source = $PSScriptRoot
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT

function Stop-Install([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath (Join-Path $source 'Mods\Gh3Skin.dll'))) {
    Stop-Install "Mods\Gh3Skin.dll is not next to this script. Run the script from the release folder."
}

if ($GameDir -eq '') {
    if (Test-Path -LiteralPath (Join-Path (Get-Location) 'Clone Hero.exe')) { $GameDir = (Get-Location).Path }
    else { $GameDir = (Read-Host 'Clone Hero folder (the folder with Clone Hero.exe)').Trim().Trim('"') }
}
if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) { Stop-Install "The folder $GameDir does not exist." }
$GameDir = (Resolve-Path -LiteralPath $GameDir).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Clone Hero.exe'))) {
    Stop-Install "$GameDir does not have Clone Hero.exe. Give the Windows Clone Hero folder."
}

# The game writes its version string (Application.version) into globalgamemanagers.
$managers = Join-Path $GameDir 'Clone Hero_Data\globalgamemanagers'
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($managers))
if (-not $text.Contains($gameVersion)) {
    Stop-Install "This Clone Hero is not $gameVersion. The mod works only with $gameVersion."
}

if ($onWindows -and (Get-Process -Name 'Clone Hero' -ErrorAction SilentlyContinue)) {
    Stop-Install 'Clone Hero is running. Close the game, then run this script again.'
}

$melonDll = Join-Path $GameDir 'MelonLoader\net6\MelonLoader.dll'
$installedMelon = ''
if ((Test-Path -LiteralPath $melonDll) -and (Test-Path -LiteralPath (Join-Path $GameDir 'version.dll'))) {
    $installedMelon = [Reflection.AssemblyName]::GetAssemblyName($melonDll).Version.ToString(3)
}
if ($installedMelon -ne $melonVersion) {
    if ($installedMelon -ne '') { Write-Host "MelonLoader $installedMelon is installed. The mod needs $melonVersion; installing $melonVersion." }
    $zip = $MelonLoaderZip
    if ($zip -eq '') {
        $zip = Join-Path ([IO.Path]::GetTempPath()) "MelonLoader-$melonVersion.x64.zip"
        Write-Host "Downloading MelonLoader $melonVersion from $melonUrl"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $melonUrl -OutFile $zip -UseBasicParsing
    }
    if (-not (Test-Path -LiteralPath $zip)) { Stop-Install "$zip does not exist." }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    if ($hash -ne $melonSha256) { Stop-Install "$zip is not the MelonLoader $melonVersion x64 release (SHA-256 $hash)." }
    Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force
    Write-Host "Installed MelonLoader $melonVersion"
}
else { Write-Host "MelonLoader $melonVersion is installed" }

# Without this setting, MelonLoader contacts api.melonloader.com at each start (docs/VERIFICATION.md#test-game). The first
# start needs the network to download the generator files, so set it only after MelonLoader generated its assemblies.
$generated = Test-Path -LiteralPath (Join-Path $GameDir 'MelonLoader\Il2CppAssemblies')
$loaderConfig = Join-Path $GameDir 'UserData\Loader.cfg'
if ($generated -and (Test-Path -LiteralPath $loaderConfig)) {
    $config = [IO.File]::ReadAllText($loaderConfig)
    if ($config -match '(?m)^force_offline_generation\s*=\s*false') {
        [IO.File]::WriteAllText($loaderConfig, ($config -replace '(?m)^force_offline_generation\s*=\s*false', 'force_offline_generation = true'))
        Write-Host 'Set force_offline_generation = true in UserData\Loader.cfg'
    }
}

$mods = Join-Path $GameDir 'Mods'
$skin = Join-Path $mods 'GH3Skin'
# The release Mods folder is complete (install.sh --release writes it from its skin_folders list), so remove the old
# skin files first: earlier versions had other asset folders, flat files, counter-tuning.json, and the older install
# (assets\stages, assets\libraries) that the packages replace. Player choices and the cache are in UserData and stay.
foreach ($old in @('assets', 'Packages', 'render', 'counter-tuning.json')) {
    $path = Join-Path $skin $old
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
$from = (Resolve-Path -LiteralPath (Join-Path $source 'Mods')).ProviderPath
$count = 0
foreach ($file in Get-ChildItem -LiteralPath $from -Recurse -File) {
    $relative = $file.FullName.Substring($from.Length).TrimStart('\', '/')
    $target = Join-Path $mods $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    $count++
}
if ($onWindows) { Get-ChildItem -LiteralPath $skin -Recurse -File | Unblock-File; Unblock-File -LiteralPath (Join-Path $mods 'Gh3Skin.dll') }
Write-Host "Installed the GH3 gameplay skin ($count files) in $mods"

if (-not $generated) {
    Write-Host 'Start Clone Hero now. The first start takes some minutes: MelonLoader prepares its files (this needs the internet), and the mod imports the stages.'
    Write-Host 'After the first start, close the game and run this script again. It then stops the MelonLoader network check at each start.'
}
else {
    Write-Host 'Start Clone Hero. The first start after an update imports the stages, which takes some time.'
}
