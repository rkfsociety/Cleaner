$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "..\Cleaner.csproj"
$outputPath = Join-Path $PSScriptRoot "..\publish\win-x64"

dotnet publish $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "Публикация Cleaner завершилась с ошибкой: $LASTEXITCODE"
}

Write-Host "Published: $outputPath\Cleaner.exe"
