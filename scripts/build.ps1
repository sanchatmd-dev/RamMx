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
    Copy-Item -LiteralPath 'README.md','Context.md' -Destination 'artifacts/RamMx'
    New-Item -ItemType Directory -Path 'artifacts/RamMx/docs' -Force | Out-Null
    Copy-Item -LiteralPath 'docs/DESIGN.md','docs/TESTING.md' -Destination 'artifacts/RamMx/docs'
    $rammxCommit = 'unknown'
    $rammxDirty = $null
    if ((Get-Command git -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath '.git')) {
        $rammxCommit = (git rev-parse HEAD).Trim()
        $rammxDirty = [bool](git status --porcelain)
    }
    [ordered]@{ Version = '0.1.0-preview'; SourceCommit = $rammxCommit; SourceDirty = $rammxDirty; BuiltAtUtc = [DateTime]::UtcNow.ToString('O'); Runtime = 'win-x64'; SelfContained = $true } |
        ConvertTo-Json | Set-Content -LiteralPath 'artifacts/RamMx/build-info.json' -Encoding utf8
    $rammxZip = Join-Path $rammxRoot 'artifacts\RamMx-win-x64.zip'
    Compress-Archive -Path (Join-Path $rammxRoot 'artifacts\RamMx\*') -DestinationPath $rammxZip -Force
    $rammxHash = Get-FileHash -Algorithm SHA256 -LiteralPath $rammxZip
    ($rammxHash.Hash + '  RamMx-win-x64.zip') | Set-Content -LiteralPath ($rammxZip + '.sha256.txt') -Encoding ascii
    $rammxHash | Format-List
}
