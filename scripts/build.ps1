param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$rammxRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $rammxRoot
$rammxLocalDotnet = Join-Path $rammxRoot '.tools\dotnet\dotnet.exe'
$rammxDotnet = if (Test-Path -LiteralPath $rammxLocalDotnet) { $env:DOTNET_ROOT = Split-Path $rammxLocalDotnet; $rammxLocalDotnet } else { 'dotnet' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $rammxDotnet restore RamMx.sln
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& $rammxDotnet build RamMx.sln -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& $rammxDotnet test tests\RamMx.Tests\RamMx.Tests.csproj -c Release --no-build --logger 'trx;LogFileName=RamMx.trx'
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if ($Publish) {
    & $rammxDotnet publish src\RamMx.App\RamMx.App.csproj -c Release -r win-x64 --self-contained true -o artifacts\RamMx
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $rammxZip = Join-Path $rammxRoot 'artifacts\RamMx-win-x64.zip'
    Compress-Archive -Path (Join-Path $rammxRoot 'artifacts\RamMx\*') -DestinationPath $rammxZip -Force
    Get-FileHash -Algorithm SHA256 -LiteralPath $rammxZip | Format-List
}
