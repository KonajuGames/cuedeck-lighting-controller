# Builds a release package of Cuedeck: a self-contained build for 64-bit Windows (it carries the .NET runtime, so
# nothing else needs installing), with the README, user guide and licence, zipped as
# artifacts\Cuedeck-<version>-win-x64.zip. The version is the project's <Version>, in Cuedeck\Cuedeck.csproj.
#
#     powershell -File tools\package.ps1 [-SkipTests]
#
# The tests run first, and nothing's packaged if any fail. cue-framework must be checked out beside this repository.

param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Cuedeck\Cuedeck.csproj'
$runtime = 'win-x64'

$version = & dotnet msbuild $project -getProperty:Version
if ($LASTEXITCODE -ne 0 -or -not $version) { throw "Couldn't read the version from $project" }
$version = "$version".Trim()
$name = "Cuedeck-$version-$runtime"
$artifacts = Join-Path $root 'artifacts'
$folder = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"

if (-not $SkipTests) {
    & dotnet test (Join-Path $root 'Cuedeck.Tests\Cuedeck.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'The tests failed, so nothing was packaged' }
}

if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

& dotnet publish $project -c Release -r $runtime -o $folder -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Packages may bring their debug symbols along; nobody running the app needs them.
Get-ChildItem $folder -Filter *.pdb -Recurse | Remove-Item

Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $folder
# The README shows the screenshot and links the user guide and feature list, which show the pictures in docs\img.
$docs = Join-Path $folder 'docs'
New-Item -ItemType Directory $docs -Force | Out-Null
Copy-Item (Join-Path $root 'docs\screenshot.png'), (Join-Path $root 'docs\USER_GUIDE.md'), (Join-Path $root 'docs\FEATURES.md') $docs
Copy-Item (Join-Path $root 'docs\img') $docs -Recurse

Compress-Archive -Path $folder -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packaged $zip ($size MB)"
