param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $projectRoot "artifacts\publish\$Runtime"
$installerDirectory = Join-Path $projectRoot 'artifacts\installer'
$iconScript = Join-Path $PSScriptRoot 'Generate-Icon.ps1'
$installerScript = Join-Path $PSScriptRoot 'CrtSharp.iss'

$isccCandidates = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $command = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($command) { $iscc = $command.Source }
}
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install JRSoftware.InnoSetup first.'
}

& $iconScript
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }
if (Test-Path $installerDirectory) { Remove-Item $installerDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

Push-Location $projectRoot
try {
    & 'C:\Program Files\nodejs\npm.cmd' ci
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet publish 'CrtSharp.csproj' `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $publishDirectory `
        -p:PublishReadyToRun=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $iscc $installerScript
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

$installer = Get-ChildItem $installerDirectory -Filter '*.exe' | Select-Object -First 1
if (-not $installer) { throw 'Installer output was not created.' }
Write-Output "Installer: $($installer.FullName)"
Write-Output "Size: $([Math]::Round($installer.Length / 1MB, 2)) MB"
Write-Output "SHA256: $((Get-FileHash $installer.FullName -Algorithm SHA256).Hash)"
