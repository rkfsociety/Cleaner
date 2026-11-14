param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Cleaner.csproj"
$publishScript = Join-Path $PSScriptRoot "publish-windows.ps1"
$publishDir = Join-Path $repoRoot "publish\win-x64"
$installerScript = Join-Path $repoRoot "installer\Cleaner.iss"
$outputDir = Join-Path $repoRoot "publish\installer"

$version = [string](dotnet msbuild $projectPath -nologo -getProperty:Version)
if ($LASTEXITCODE -ne 0) {
    throw "Не удалось прочитать версию из Cleaner.csproj."
}

$version = $version.Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Версия должна иметь формат MAJOR.MINOR.PATCH: $version"
}

if (-not $SkipPublish) {
    & $publishScript
}

$publishedExe = Join-Path $publishDir "Cleaner.exe"
if (!(Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
    throw "Не найден опубликованный файл $publishedExe. Запустите scripts\publish-windows.ps1."
}

$iscc = Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}

if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) не найден. Установите его (например: winget install JRSoftware.InnoSetup) и повторите попытку."
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

& $iscc `
    "/DMyAppVersion=$version" `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    "/DOutputBaseFilename=Cleaner-Setup" `
    $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Сборка установщика Inno Setup завершилась с ошибкой: $LASTEXITCODE"
}

$setupExe = Join-Path $outputDir "Cleaner-Setup.exe"
if (!(Test-Path -LiteralPath $setupExe -PathType Leaf)) {
    throw "Установщик $setupExe не был создан."
}

Write-Host "Installer created: $setupExe"
