param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$TargetPath,
    [Parameter(Mandatory = $true)][string]$ShortcutPath,
    [Parameter(Mandatory = $true)][string]$FallbackPath,
    [Parameter(Mandatory = $true)][string]$FailureMarkerPath,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
$targetDirectory = Split-Path -Parent $TargetPath
$backupPath = Join-Path $targetDirectory 'DiskPulse.exe.previous'
$hadOriginal = $false

try {
    $parent = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $parent) {
        $parent.WaitForExit()
    }

    if (!(Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw 'Не найден подготовленный файл обновления.'
    }

    [System.IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
        if (Test-Path -LiteralPath $backupPath) {
            Remove-Item -LiteralPath $backupPath -Force
        }
        Move-Item -LiteralPath $TargetPath -Destination $backupPath
        $hadOriginal = $true
    }

    Move-Item -LiteralPath $PackagePath -Destination $TargetPath

    $programsDirectory = Split-Path -Parent $ShortcutPath
    [System.IO.Directory]::CreateDirectory($programsDirectory) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $targetDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Save()
    [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)

    Start-Process -FilePath $TargetPath | Out-Null
    if (Test-Path -LiteralPath $backupPath) {
        Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
    }
}
catch {
    $installError = $_.Exception.Message
    if ($hadOriginal -and (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        try {
            if (Test-Path -LiteralPath $TargetPath) {
                Remove-Item -LiteralPath $TargetPath -Force
            }
            Move-Item -LiteralPath $backupPath -Destination $TargetPath -Force
        }
        catch {
            $installError += "; не удалось восстановить прежний файл: $($_.Exception.Message)"
        }
    }

    try {
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $FailureMarkerPath)) | Out-Null
        [System.IO.File]::WriteAllText($FailureMarkerPath, "Не удалось установить DiskPulse: $installError", [System.Text.Encoding]::UTF8)
    }
    catch {
        $markerError = $_.Exception.Message
        try {
            [System.IO.Directory]::CreateDirectory((Split-Path -Parent $LogPath)) | Out-Null
            [System.IO.File]::AppendAllText($LogPath, "[$([DateTime]::Now.ToString('s'))] Не удалось записать сообщение об ошибке установки: $markerError`r`n", [System.Text.Encoding]::UTF8)
        }
        catch { [Console]::Error.WriteLine("Не удалось записать журнал установки: $($_.Exception.Message)") }
    }

    $launchPath = if (Test-Path -LiteralPath $TargetPath -PathType Leaf) { $TargetPath } else { $FallbackPath }
    if (Test-Path -LiteralPath $launchPath -PathType Leaf) {
        try { Start-Process -FilePath $launchPath | Out-Null }
        catch {
            $launchError = $_.Exception.Message
            try {
                [System.IO.Directory]::CreateDirectory((Split-Path -Parent $LogPath)) | Out-Null
                [System.IO.File]::AppendAllText($LogPath, "[$([DateTime]::Now.ToString('s'))] Не удалось перезапустить DiskPulse после ошибки установки: $launchError`r`n", [System.Text.Encoding]::UTF8)
            }
            catch { [Console]::Error.WriteLine("Не удалось записать журнал перезапуска: $($_.Exception.Message)") }
        }
    }
}
finally {
    if (Test-Path -LiteralPath $PackagePath -PathType Leaf) {
        Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $PSScriptRoot -Recurse -Force -ErrorAction SilentlyContinue
}
