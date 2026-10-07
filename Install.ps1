$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
try {
    $sourceExe = Join-Path $PSScriptRoot 'LanFileHall.exe'
    $installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\LanFileHall'
    $installedExe = Join-Path $installDirectory 'LanFileHall.exe'
    if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'LanFileHall.exe is missing. Extract the ZIP first.' }
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
    $shortcutShell = New-Object -ComObject WScript.Shell
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'LAN File Hall.lnk'
    $menuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'LAN File Hall.lnk'
    foreach ($shortcutPath in @($desktopShortcut, $menuShortcut)) {
        $shortcut = $shortcutShell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $installedExe
        $shortcut.WorkingDirectory = $installDirectory
        $shortcut.Description = 'Local network file sharing'
        $shortcut.Save()
    }
    [System.Windows.Forms.MessageBox]::Show('Installed. Open LAN File Hall from your desktop.', 'LAN File Hall') | Out-Null
} catch {
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Installation failed') | Out-Null
    exit 1
}
