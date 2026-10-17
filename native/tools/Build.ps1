param([switch]$SkipSmoke)
# Builds and verifies the Court Creator Rust preview. Node/pnpm, Rust and the MSVC tools are
# development dependencies only; none of them is needed by the built application.
# Native tools write progress to stderr; failures are detected through exit codes instead.
$ErrorActionPreference = 'Continue'
$native = Split-Path $PSScriptRoot -Parent
$workspace = Split-Path (Split-Path $native -Parent) -Parent
$rust = Join-Path $workspace '2k Canvas/source/artifacts/toolchains/rust'
$evidence = Join-Path $native 'target/verification'
$originalPath = $env:PATH
try {
    if (Test-Path (Join-Path $rust 'cargo/bin/cargo.exe')) {
        $env:CARGO_HOME = Join-Path $rust 'cargo'
        $env:RUSTUP_HOME = Join-Path $rust 'rustup'
        $env:PATH = "$env:CARGO_HOME/bin;$env:PATH"
    }
    $pnpm = (Get-Command pnpm -ErrorAction SilentlyContinue).Source
    if (!$pnpm) { $pnpm = Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/bin/fallback/pnpm.cmd' }
    if (!(Test-Path -LiteralPath $pnpm)) { throw 'Install Node and pnpm for development. They are not packaged with the application.' }
    $node = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (!$node) { throw 'Install Node for development. It is not packaged with the application.' }
    # cmd.exe cannot search a very long PATH (pnpm scripts would not find tsc/vite), so the
    # frontend steps also start from a minimal one.
    $env:PATH = "$(Split-Path $node);$(Split-Path $pnpm);$env:SystemRoot\system32;$env:SystemRoot"
    & $pnpm --dir $native install --frozen-lockfile --ignore-scripts
    if ($LASTEXITCODE) { throw 'Frontend dependency installation failed.' }
    & $pnpm --dir $native run build
    if ($LASTEXITCODE) { throw 'Frontend type-check or build failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'Visual C++ Build Tools are required for development.' }
    $vcvars = Join-Path $vs 'VC/Auxiliary/Build/vcvars64.bat'
    $cargoPath = "$env:CARGO_HOME/bin"
    Push-Location $native
    try {
        # cmd/vcvars reject very long PATH values, so the build starts from a minimal one.
        $env:PATH = "$cargoPath;$env:SystemRoot\system32;$env:SystemRoot;${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
        & cmd.exe /d /c "`"$vcvars`" >nul && cargo test --locked -p court-core --release && cargo build --locked -p court-creator-desktop --release"
        if ($LASTEXITCODE) { throw 'Rust tests or the desktop build failed.' }
    } finally { Pop-Location }
    if ($SkipSmoke) { return }
    $exe = Join-Path $native 'target/release/court-creator-desktop.exe'
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    $fixture = Join-Path (Split-Path $native -Parent) 'outputs/v1717/hardwood-tools/editable-text.court.json'
    foreach ($case in @(@{ Name='smoke'; Flags=@() }, @{ Name='smoke-dark'; Flags=@('--smoke-dark') }, @{ Name='smoke-compact'; Flags=@('--smoke-compact') })) {
        $report = Join-Path $evidence ("{0}.json" -f $case.Name)
        $data = Join-Path $evidence ("{0}-data" -f $case.Name)
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
        # Private data root: no personal preferences, recovery or projects are read or written.
        $env:COURT_CREATOR_NATIVE_DATA = $data
        $env:COURT_CREATOR_NO_RECOVERY = '1'
        if (Test-Path -LiteralPath $fixture) { $env:COURT_CREATOR_SMOKE_PROJECT = $fixture } else { Remove-Item Env:COURT_CREATOR_SMOKE_PROJECT -ErrorAction SilentlyContinue }
        $arguments = @('--smoke-output', "`"$report`"") + $case.Flags
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(330000)) { try { $process.Kill() } catch {}; throw "Hidden check $($case.Name) timed out." }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) { throw "Hidden check $($case.Name) failed (exit $($process.ExitCode))." }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if (!$result.passed) { throw "Hidden checks failed. Inspect $report." }
        if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $hash) { throw 'The EXE changed during verification.' }
        $result | Add-Member -NotePropertyName buildSha256 -NotePropertyValue $hash -Force
        $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding UTF8
    }
    Write-Host 'Rust core tests, TypeScript build, desktop EXE and hidden off-screen checks passed.'
} finally {
    $env:PATH = $originalPath
    Remove-Item Env:COURT_CREATOR_NATIVE_DATA, Env:COURT_CREATOR_NO_RECOVERY, Env:COURT_CREATOR_SMOKE_PROJECT -ErrorAction SilentlyContinue
}
