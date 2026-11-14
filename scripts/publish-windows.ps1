$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "..\DiskPulse.csproj"
$outputPath = Join-Path $PSScriptRoot "..\publish\win-x64"

dotnet restore $projectPath --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Восстановление зависимостей DiskPulse завершилось с ошибкой: $LASTEXITCODE"
}

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $outputPath `
    --no-restore

if ($LASTEXITCODE -ne 0) {
    throw "Публикация DiskPulse завершилась с ошибкой: $LASTEXITCODE"
}

Write-Host "Published: $outputPath\DiskPulse.exe"
