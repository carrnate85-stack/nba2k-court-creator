# Installs the verified Rust preview beside (never over) the existing Court Creator and adds a
# separate desktop shortcut. The existing launcher and its caches are left untouched.
$ErrorActionPreference = 'Stop'
$native = Split-Path $PSScriptRoot -Parent
$binary = Join-Path $native 'target/release/court-creator-desktop.exe'
$evidence = Join-Path $native 'target/verification'
$destinationFolder = Join-Path $env:LOCALAPPDATA 'Programs/NBA 2K Court Creator Rust Preview'
$destination = Join-Path $destinationFolder 'NBA 2K Court Creator Rust Preview.exe'
if (!(Test-Path -LiteralPath $binary)) { throw 'Build the preview before installing.' }
$hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
foreach ($case in @('smoke', 'smoke-dark', 'smoke-compact')) {
    $check = Get-Content -LiteralPath (Join-Path $evidence "$case.json") -Raw | ConvertFrom-Json
    if (!$check.passed) { throw "Preview verification failed: $case." }
    if ($check.buildSha256 -ne $hash) { throw "The preview changed since verification ($case). Run tools/Build.ps1 again." }
}
if (Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $destination }) { throw 'Save and close the Rust preview before replacing it.' }
New-Item -ItemType Directory -Force -Path $destinationFolder | Out-Null
if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) {
    Copy-Item -LiteralPath $destination -Destination (Join-Path $destinationFolder ('previous-{0}.exe' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')))
}
Copy-Item -LiteralPath $binary -Destination $destination -Force
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw 'Installed EXE checksum mismatch.' }
# Run the installed copy hidden from TEMP to prove the UI is embedded (no dev server, no Node).
$report = Join-Path $evidence 'installed-smoke.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
$env:COURT_CREATOR_NATIVE_DATA = Join-Path $evidence 'installed-data'
$env:COURT_CREATOR_NO_RECOVERY = '1'
try {
    $process = Start-Process -FilePath $destination -WorkingDirectory $env:TEMP -ArgumentList @('--smoke-output', "`"$report`"") -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(330000)) { try { $process.Kill() } catch {}; throw 'Installed preview check timed out.' }
} finally { Remove-Item Env:COURT_CREATOR_NATIVE_DATA, Env:COURT_CREATOR_NO_RECOVERY -ErrorAction SilentlyContinue }
if ($process.ExitCode -ne 0 -or !((Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).passed)) { throw 'Installed preview verification failed.' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'NBA 2K Court Creator Rust Preview.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $destination
$link.WorkingDirectory = $destinationFolder
$link.IconLocation = "$destination,0"
$link.Description = 'NBA 2K Court Creator (Rust/Tauri preview)'
$link.Save()
@{
    version = (Get-Content -LiteralPath (Join-Path $native 'src-tauri/tauri.conf.json') -Raw | ConvertFrom-Json).version
    stack = 'Rust + Tauri 2 + React + TypeScript'; executable = $destination; shortcut = $shortcut; sha256 = $hash
    sizeBytes = (Get-Item -LiteralPath $destination).Length; installedAtUtc = [DateTime]::UtcNow.ToString('o')
    hiddenStartupPassed = $true; bundledDotNet = $false; bundledPython = $false; bundledNode = $false; bundledWebView2 = $false
    replacesExistingApp = $false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'preview-receipt.json') -Encoding UTF8
Write-Host "Verified preview installed: $destination"
Write-Host "Shortcut: $shortcut"
