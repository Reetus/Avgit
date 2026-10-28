<#
.SYNOPSIS
    Builds a Velopack release of Avgit for Windows: Avgit-win-Setup.exe, a
    portable zip and the release feed (releases.win.json, full/delta .nupkg).

.DESCRIPTION
    Publishes the NativeAOT build (or -Profile SingleFile), then packs it with
    the vpk version pinned in dotnet-tools.json. Setup.exe installs per user to
    %LocalAppData%\Avgit without admin rights and adds Start menu and desktop
    shortcuts; the app can then update itself from the release feed.

    Keep earlier releases in the output folder so vpk can build delta packages
    against them.

.EXAMPLE
    pwsh branding/windows/build-setup.ps1
.EXAMPLE
    pwsh branding/windows/build-setup.ps1 -Version 0.2.0 -SignParams '/a /fd sha256 /tr http://timestamp.digicert.com /td sha256'
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in Avgit.App/Avgit.App.csproj.
    [string]$Version,
    # Defaults to this machine's architecture (win-x64 or win-arm64).
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime,
    # Velopack channel; defaults to the runtime (win-x64). Windows and Linux
    # packages share one GitHub release, so each needs its own channel or their
    # file names collide. Installed copies update from the channel they came from.
    [string]$Channel,
    # Optional markdown file with release notes.
    [string]$ReleaseNotes,
    # Defaults to artifacts/releases/<runtime>.
    [string]$Output,
    # NativeAot needs the Visual Studio C++ workload; SingleFile does not.
    [ValidateSet('NativeAot', 'SingleFile')]
    [string]$Profile = 'NativeAot',
    # Optional signtool.exe parameters; signs Avgit.exe, Setup.exe and the updater.
    [string]$SignParams,
    # Optional Azure Trusted Signing metadata.json (alternative to -SignParams).
    [string]$AzureTrustedSignFile
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $repoRoot
try {
    if (-not $Runtime) {
        $Runtime = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    }
    if (-not $Version) {
        $Version = (dotnet msbuild Avgit.App/Avgit.App.csproj -getProperty:Version).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $Version) { throw 'Could not read <Version> from Avgit.App.csproj.' }
    }
    if (-not $Channel) { $Channel = $Runtime }
    if (-not $Output) { $Output = Join-Path $repoRoot "artifacts\releases\$Runtime" }

    if ($Profile -eq 'NativeAot') {
        # The ILCompiler locates the MSVC linker through vcvarsall, which in
        # recent Visual Studio versions calls vswhere.exe by name; it is not on
        # PATH outside a Developer prompt.
        $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        if ((Test-Path (Join-Path $installer 'vswhere.exe')) -and ($env:PATH -notlike "*$installer*")) {
            $env:PATH = "$installer;$env:PATH"
        }
    }

    $publishDir = if ($Profile -eq 'NativeAot') { "artifacts\publish\aot\$Runtime" } else { "artifacts\publish\singlefile\$Runtime" }
    Write-Host "Publishing Avgit $Version for $Runtime ($Profile)"
    # Clear stale files so nothing from an earlier build ends up in the package.
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    dotnet publish Avgit.App -c Release -r $Runtime "-p:PublishProfile=$Profile" "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path (Join-Path $publishDir 'Avgit.exe'))) { throw "Avgit.exe not found in $publishDir." }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    $packArgs = @(
        'vpk', 'pack',
        '--packId', 'Avgit',
        '--packVersion', $Version,
        '--packTitle', 'Avgit',
        '--packAuthors', 'Avgit',
        '--packDir', $publishDir,
        '--mainExe', 'Avgit.exe',
        '--icon', 'Avgit.App\Assets\avgit.ico',
        '--runtime', $Runtime,
        '--channel', $Channel,
        '--outputDir', $Output
    )
    if ($ReleaseNotes) { $packArgs += @('--releaseNotes', $ReleaseNotes) }
    if ($SignParams) { $packArgs += @('--signParams', $SignParams) }
    if ($AzureTrustedSignFile) { $packArgs += @('--azureTrustedSignFile', $AzureTrustedSignFile) }

    dotnet @packArgs
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE." }

    Write-Host "Release written to $Output"
    Get-ChildItem $Output -File | Sort-Object Name | ForEach-Object {
        Write-Host ("  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
    }
}
finally {
    Pop-Location
}
