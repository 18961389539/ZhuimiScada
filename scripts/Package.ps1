$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$outputDirectory = Join-Path $projectRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$zipPath = Join-Path $outputDirectory 'PLCScada-phase1.zip'
$files = Get-ChildItem -LiteralPath $projectRoot -File -Recurse -Force | Where-Object {
    $relative = [IO.Path]::GetRelativePath($projectRoot, $_.FullName)
    $relative -notmatch '(^|[\\/])(bin|obj|App_Data|logs|artifacts|\.work|\.git|\.vs)([\\/]|$)' -and
    $_.Name -notlike '*.user' -and $_.Extension -notin @('.db', '.db-wal', '.db-shm')
}
$archive = [IO.Compression.ZipArchive]::new([IO.File]::Open($zipPath, [IO.FileMode]::Create), [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "PLCScada/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }
Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
