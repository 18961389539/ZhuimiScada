$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $projectRoot
try {
    dotnet restore PLCScada.sln
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    dotnet run --project PLCScada.Web --launch-profile http
    if ($LASTEXITCODE -ne 0) { throw 'PLCScada stopped with an error.' }
}
finally { Pop-Location }
