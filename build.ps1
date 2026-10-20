<#
.SYNOPSIS
    One-command build for Chao Desk.
.DESCRIPTION
    1. Builds the host solution (and runs tests unless -SkipTests).
    2. Copies each wallpaper folder in wallpapers/ (static web pages + wallpaper.json) into
       <host output>/wallpapers/<id>/, replacing whatever was there.
    With -Package (implies Release):
    3. Publishes a framework-dependent win-x64 build to dist/app (needs the .NET 8 Desktop Runtime;
       about 4 MB instead of ~146 MB self-contained). Diagnostic wallpapers are excluded.
    4. Compiles installer/chaodesk.iss with Inno Setup 6 into dist/ChaoDeskSetup-<version>.exe.
    With -Deploy (implies Release): publishes to dist/app as in step 3 and copies it over the installed
    app (location from the installer's uninstall entry), restarting Chao Desk. No installer needed.
.PARAMETER Configuration
    Debug (default) or Release. -Package always uses Release.
.PARAMETER SkipTests
    Skip the xUnit tests.
.PARAMETER Package
    Produce the app in dist/app and the installer in dist/.
.PARAMETER Deploy
    Update the installed app in place (after the installer has been run once).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [switch]$Package,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if ($Package -or $Deploy) { $Configuration = 'Release' }

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $Name (exit code $LASTEXITCODE)" }
}

# ---- 1. Host -------------------------------------------------------------
# A running Chao Desk from this output folder locks ChaoDesk.exe; ask it to exit cleanly first.
$running = Get-Process ChaoDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $root 'host\src\*') }
if ($running) {
    Write-Host "==> Stopping the running Chao Desk ($($running.Path))" -ForegroundColor Cyan
    Start-Process $running[0].Path -ArgumentList '--exit' -Wait
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# ---- 0. OCR models -------------------------------------------------------
# PP-OCRv5 is not kept in git: download what models/ocr/models.json lists and check each SHA-256.
Invoke-Step 'OCR models' {
    $modelDir = Join-Path $root 'models/ocr'
    $manifest = Get-Content (Join-Path $modelDir 'models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($m in $manifest.files) {
        $file = Join-Path $modelDir $m.name
        if ((Test-Path $file) -and ((Get-FileHash $file -Algorithm SHA256).Hash -eq $m.sha256)) { continue }
        Write-Host "    downloading $($m.name)"
        Invoke-WebRequest -Uri $m.url -OutFile "$file.part" -UseBasicParsing
        $hash = (Get-FileHash "$file.part" -Algorithm SHA256).Hash
        if ($hash -ne $m.sha256) { Remove-Item "$file.part"; throw "$($m.name): SHA-256 $hash does not match the manifest" }
        Move-Item -Force "$file.part" $file
    }
}

$sln = Join-Path $root 'host/ChaoDesk.sln'
$proj = Join-Path $root 'host/src/ChaoDesk.Host/ChaoDesk.Host.csproj'
Invoke-Step "Build host ($Configuration)" { dotnet build $sln -c $Configuration --nologo -v minimal }
if (-not $SkipTests) {
    # Wallpaper engine tests (node --test), for wallpapers that have a tests/ folder.
    Get-ChildItem (Join-Path $root 'wallpapers') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'tests') } | ForEach-Object {
        $files = @((Get-ChildItem (Join-Path $_.FullName 'tests') -Filter *.test.cjs).FullName) # an array even for one file
        Invoke-Step "Wallpaper tests ($($_.Name))" { node --test @files }
    }
}
if (-not $SkipTests) { Invoke-Step 'Host tests' { dotnet test $sln -c $Configuration --no-build --nologo -v minimal } }

$hostOut = Join-Path $root "host/src/ChaoDesk.Host/bin/$Configuration/net8.0-windows"

# ---- 2. Copy wallpapers into the host output -----------------------------
function Copy-Wallpapers([string]$Destination, [switch]$ExcludeDiagnostic) {
    $wpRoot = Join-Path $Destination 'wallpapers'
    # Start clean so removed wallpapers never linger in the output.
    if (Test-Path $wpRoot) { Remove-Item -Recurse -Force $wpRoot }
    New-Item -ItemType Directory -Force $wpRoot | Out-Null
    Get-ChildItem (Join-Path $root 'wallpapers') -Directory | ForEach-Object {
        $manifest = Join-Path $_.FullName 'wallpaper.json'
        $dist = Join-Path $_.FullName 'dist'
        # Built wallpapers (package.json) ship their dist/; static ones (no package.json) ship the folder as is.
        if (-not (Test-Path (Join-Path $_.FullName 'package.json'))) { $dist = $_.FullName }
        if (-not ((Test-Path $manifest) -and (Test-Path $dist))) { return }
        $isDiagnostic = [bool]((Get-Content $manifest -Raw -Encoding UTF8 | ConvertFrom-Json).diagnostic)
        if ($ExcludeDiagnostic -and $isDiagnostic) { Write-Host "    skipped diagnostic wallpaper '$($_.Name)'"; return }
        $target = Join-Path $wpRoot $_.Name
        if (Test-Path $target) { Remove-Item -Recurse -Force $target }
        Copy-Item -Recurse $dist $target
        # Development-only files never ship.
        foreach ($dev in 'tests', 'node_modules') { $p = Join-Path $target $dev; if (Test-Path $p) { Remove-Item -Recurse -Force $p } }
        Copy-Item $manifest $target
        Write-Host "    copied wallpaper '$($_.Name)' -> $target"
    }
}
Invoke-Step 'Copy wallpapers' { Copy-Wallpapers $hostOut }

if (-not ($Package -or $Deploy)) {
    Write-Host "Build complete. Run: $hostOut\ChaoDesk.exe" -ForegroundColor Green
    return
}

# ---- 3. Self-contained publish ---------------------------------------------
$distRoot = Join-Path $root 'dist'
$appOut = Join-Path $distRoot 'app'
if (Test-Path $appOut) { Remove-Item -Recurse -Force $appOut }
Invoke-Step 'Publish host (framework-dependent, win-x64)' {
    dotnet publish $proj -c Release -r win-x64 --self-contained false -o $appOut --nologo -v minimal `
        -p:DebugType=none -p:SatelliteResourceLanguages=en
}
Invoke-Step 'Copy wallpapers into package' { Copy-Wallpapers $appOut -ExcludeDiagnostic }
# ONNX Runtime (text recognition) needs the Visual C++ runtime, which a clean Windows may lack. Microsoft allows
# these files to be shipped next to the app (app-local deployment); copy this machine's x64 ones.
Invoke-Step 'Visual C++ runtime for OCR' {
    foreach ($dll in 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
        $src = Join-Path $env:windir "System32\$dll"
        if (-not (Test-Path $src)) { throw "$dll was not found in System32; install the Visual C++ Redistributable (x64) to build the package." }
        Copy-Item $src $appOut -Force
    }
}
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $appOut

if ($Deploy) {
    # ---- 3b. Update the installed app in place ----------------------------------------------------
    $entry = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' | Get-ItemProperty |
        Where-Object { $_.DisplayName -eq 'Chao Desk' -and $_.InstallLocation } | Select-Object -First 1
    if (-not $entry) { throw 'Chao Desk is not installed; run ./build.ps1 -Package and the installer once first.' }
    $installDir = $entry.InstallLocation.TrimEnd('\')
    $exe = Join-Path $installDir 'ChaoDesk.exe'
    if (Test-Path $exe) {
        Write-Host "==> Stopping the installed Chao Desk" -ForegroundColor Cyan
        Start-Process $exe -ArgumentList '--exit' -Wait
        $installed = Get-Process ChaoDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
        $installed | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
        # Still running (e.g. a second instance)? Stop it, or its DLLs stay locked.
        Get-Process ChaoDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    # Replace the wallpapers wholesale; copy the app files over (the uninstaller's files stay).
    $wp = Join-Path $installDir 'wallpapers'
    if (Test-Path $wp) { Remove-Item -Recurse -Force $wp }
    Copy-Item -Recurse -Force (Join-Path $appOut '*') $installDir
    # Explorer caches exe icons (taskbar, Task Manager, shortcuts); refresh it so a new app icon shows.
    & "$env:windir\System32\ie4uinit.exe" -show
    # Through Explorer, so the app runs at normal integrity even when this script is elevated (as at sign-in).
    Start-Process explorer.exe -ArgumentList "`"$exe`""
    Write-Host "Deployed to $installDir and restarted Chao Desk." -ForegroundColor Green
    return
}

# ---- 4. Installer ----------------------------------------------------------
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup) and run ./build.ps1 -Package again. The app is ready in $appOut."
}
Invoke-Step "Compile installer (Inno Setup, v$version)" {
    & $iscc /Qp "/DAppVersion=$version" "/DSourceDir=$appOut" "/DOutputDir=$distRoot" (Join-Path $root 'installer/chaodesk.iss')
}
$setup = Join-Path $distRoot "ChaoDeskSetup-$version.exe"
Write-Host ("Package complete: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
