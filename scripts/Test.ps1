$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $projectRoot
try {
    dotnet restore PLCScada.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build PLCScada.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test PLCScada.sln -c Release --no-build --logger 'trx;LogFileName=phase1.trx' --results-directory artifacts/test-results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally { Pop-Location }
