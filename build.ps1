<#
.SYNOPSIS
  Runs the tests and publishes PortBridge as a single PortBridge.exe in .\artifacts\PortBridge.

.PARAMETER FrameworkDependent
  Smaller exe that needs the .NET 10 Desktop + ASP.NET Core runtimes installed on the target PC.
  By default the exe is self-contained and runs on any 64-bit Windows 10/11 / Server 2019+.

.PARAMETER SkipTests
  Publish without running the test suite.

.PARAMETER Installer
  Also builds .\artifacts\installer\PortBridge-Setup-<version>.exe with Inno Setup 6
  (winget install JRSoftware.InnoSetup).
#>
param([switch]$FrameworkDependent, [switch]$SkipTests, [switch]$Installer)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test tests/PortBridge.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
    dotnet test tests/PortBridge.Host.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Host tests failed" }
}

$out = Join-Path $PSScriptRoot 'artifacts\PortBridge'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$publish = @(
    'publish', 'src/PortBridge', '-c', 'Release', '-r', 'win-x64', '-o', $out,
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none'
)
if ($FrameworkDependent) { $publish += @('--self-contained', 'false') }
else { $publish += @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true') }

dotnet @publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# The exe bundles the .NET runtime (when self-contained) and NuGet packages such as the WebView2 SDK. Ship their
# license and notice texts next to it, taken from the exact versions this publish restored.
$assets = Get-Content 'src/PortBridge/obj/project.assets.json' -Raw | ConvertFrom-Json
$packages = @($assets.packageFolders.PSObject.Properties.Name)[0]
$sources = @()
if (-not $FrameworkDependent) {
    foreach ($fw in $assets.project.frameworks.PSObject.Properties) {
        foreach ($d in $fw.Value.downloadDependencies) {
            if ($d.name -notlike '*.Runtime.win-x64') { continue }
            $v = $d.version -replace '^\[([^,\]]+).*$', '$1' # "[10.0.12, 10.0.12]"
            $sources += @{ Name = "$($d.name) $v"; Dir = Join-Path $packages "$($d.name.ToLowerInvariant())/$v" }
        }
    }
}
$shipped = ($assets.targets.PSObject.Properties | Where-Object Name -like '*/win-x64' | Select-Object -First 1).Value
foreach ($lib in $assets.libraries.PSObject.Properties | Sort-Object Name) {
    $t = $shipped.($lib.Name)
    if ($lib.Value.type -ne 'package' -or -not ($t.runtime -or $t.native -or $t.runtimeTargets)) { continue } # build-only
    $sources += @{ Name = $lib.Name -replace '/', ' '; Dir = Join-Path $packages $lib.Value.path }
}
$texts = [Collections.Specialized.OrderedDictionary]::new([StringComparer]::Ordinal) # text -> files that contain it
foreach ($s in $sources) {
    foreach ($f in Get-ChildItem $s.Dir -File | Where-Object Name -match '^(license|notice|third-?party-?notices)' | Sort-Object Name) {
        $text = ([IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n").Trim()
        $texts[$text] = @($texts[$text] | Where-Object { $_ }) + "$($s.Name) - $($f.Name)"
    }
}
$notices = [Text.StringBuilder]::new()
[void]$notices.AppendLine("PortBridge.exe includes the following third-party software. Their license and notice texts follow.")
foreach ($t in $texts.GetEnumerator()) {
    [void]$notices.AppendLine("`n$('=' * 78)`n$($t.Value -join "`n")`n$('=' * 78)`n`n$($t.Key)")
}
[IO.File]::WriteAllText((Join-Path $out 'THIRD-PARTY-NOTICES.txt'), ($notices.ToString() -replace "`r?`n", "`r`n"))
Copy-Item LICENSE (Join-Path $out 'LICENSE.txt')

Get-ChildItem $out | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
Write-Host "`nDone: $out\PortBridge.exe"

if ($Installer) {
    if ($FrameworkDependent) { throw "The installer packages the self-contained exe; build it without -FrameworkDependent." }
    $iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup)." }
    $version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
    & $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot 'installer\PortBridge.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    Write-Host "Done: $(Join-Path $PSScriptRoot "artifacts\installer\PortBridge-Setup-$version.exe")"
}
