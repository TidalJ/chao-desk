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
.PARAMETER VerifyPackage
    Only verify an existing published app; do not build, download, stop or start anything.
.PARAMETER PackageDirectory
    Directory to check with -VerifyPackage. Defaults to dist/app.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [switch]$Package,
    [switch]$Deploy,
    [switch]$VerifyPackage,
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if ($Package -or $Deploy) { $Configuration = 'Release' }

function Invoke-Step([string]$Name, [scriptblock]$Body) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body
    if ($LASTEXITCODE -ne 0) { throw "Step failed: $Name (exit code $LASTEXITCODE)" }
}

function Assert-PublishedApp([string]$Directory) {
    $folder = Get-Item -LiteralPath $Directory -ErrorAction Stop
    if (-not $folder.PSIsContainer) { throw "Package directory is not a folder: $Directory" }
    $directoryPath = $folder.FullName

    function Require-PackageFile([string]$RelativePath) {
        $path = Join-Path $directoryPath $RelativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
            throw "Package is missing a non-empty file: $RelativePath"
        }
        return $path
    }

    function Assert-X64Binary([string]$Path) {
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
        try {
            if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
                throw "Package has an invalid Windows binary: $Path"
            }
            $reader.BaseStream.Position = 60
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 64 -or $peOffset -gt $reader.BaseStream.Length - 6) {
                throw "Package has an invalid PE header: $Path"
            }
            $reader.BaseStream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
                throw "Package requires an x64 Windows binary: $Path"
            }
        }
        finally { $reader.Dispose() }
    }

    $required = @(
        'ChaoDesk.exe', 'ChaoDesk.dll', 'ChaoDesk.deps.json', 'ChaoDesk.runtimeconfig.json',
        'Microsoft.ML.OnnxRuntime.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll',
        'THIRD-PARTY-NOTICES.md'
    )
    foreach ($name in $required) { $null = Require-PackageFile $name }

    $deps = Get-Content -LiteralPath (Join-Path $directoryPath 'ChaoDesk.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $targetName = '.NETCoreApp,Version=v8.0/win-x64'
    if ($deps.runtimeTarget.name -ne $targetName) {
        throw "Package was not published for win-x64 (runtimeTarget: '$($deps.runtimeTarget.name)'). Run ./build.ps1 -Package; do not package a Debug build directory."
    }
    $targetProperty = $deps.targets.PSObject.Properties[$targetName]
    if (-not $targetProperty) { throw "Package dependency manifest has no target: $targetName" }

    $config = Get-Content -LiteralPath (Join-Path $directoryPath 'ChaoDesk.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
        $match = @($config.runtimeOptions.frameworks | Where-Object { $_.name -eq $framework -and $_.version -match '^8\.0\.\d+$' })
        if ($match.Count -ne 1) { throw "Package must require the .NET 8 Desktop Runtime: $framework 8.0.x was not found." }
    }
    if ($config.runtimeOptions.includedFrameworks) { throw 'Package must be framework-dependent, not self-contained.' }

    # RID-specific publish flattens most asset paths, while deps.json retains the NuGet paths.
    # Check the resolved target, not every architecture listed by a portable Debug build.
    $nativeNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $targetProperty.Value.PSObject.Properties) {
        foreach ($kind in 'runtime', 'native') {
            foreach ($asset in $library.Value.$kind.PSObject.Properties) {
                if ([IO.Path]::GetExtension($asset.Name) -ne '.dll') { continue }
                $name = [IO.Path]::GetFileName($asset.Name)
                $path = Join-Path $directoryPath $name
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                    $relativePath = $asset.Name.Replace('/', [IO.Path]::DirectorySeparatorChar)
                    $path = [IO.Path]::GetFullPath((Join-Path $directoryPath $relativePath))
                    if (-not $path.StartsWith($directoryPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Package dependency escapes its directory: $($asset.Name)"
                    }
                }
                if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
                    throw "Package is missing dependency $($asset.Name) from $($library.Name)."
                }
                if ($kind -eq 'native') {
                    Assert-X64Binary $path
                    $null = $nativeNames.Add($name)
                }
            }
        }
    }
    foreach ($name in 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'WebView2Loader.dll') {
        if (-not $nativeNames.Contains($name)) { throw "Package dependency manifest is missing required native library: $name" }
    }
    Assert-X64Binary (Join-Path $directoryPath 'ChaoDesk.exe')
    foreach ($name in 'msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
        Assert-X64Binary (Require-PackageFile $name)
    }

    $manifest = Get-Content -LiteralPath (Join-Path $root 'models/ocr/models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($model in $manifest.files) {
        $path = Require-PackageFile (Join-Path 'ocr' $model.name)
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $model.sha256) {
            throw "Package OCR model failed SHA-256 verification: $($model.name)"
        }
    }
    Write-Host "Verified win-x64 package, native dependencies and OCR model hashes: $directoryPath" -ForegroundColor Green
}

if ($VerifyPackage) {
    if ($Package -or $Deploy) { throw '-VerifyPackage cannot be combined with -Package or -Deploy.' }
    if (-not $PackageDirectory) { $PackageDirectory = Join-Path $root 'dist/app' }
    Assert-PublishedApp $PackageDirectory
    return
}
if ($PSBoundParameters.ContainsKey('PackageDirectory')) { throw '-PackageDirectory requires -VerifyPackage.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

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

# ---- 3. Framework-dependent publish ---------------------------------------
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
Invoke-Step 'Verify published package' { Assert-PublishedApp $appOut }

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
