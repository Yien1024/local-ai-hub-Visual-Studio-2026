# Hand-rolled VSIX packer (fully offline; no VSSDK.BuildTools / NuGet needed).
#
# Why hand-rolled: this machine cannot reach nuget.org, so Microsoft.VSSDK.BuildTools
# is unavailable and the standard VSIX packaging targets cannot run. A .vsix is just
# an OPC package (zip) and needs exactly two things to be valid:
#   1. extension.vsixmanifest  (at the package root)
#   2. [Content_Types].xml     (without it VSIXInstaller reports "not a valid VSIX")
#
# NOTE: this file must stay pure ASCII. Windows PowerShell 5.1 decodes .ps1 files using
# the ANSI codepage (GBK on this machine), so non-ASCII source silently corrupts and
# produces bizarre parse errors. Same reason the manifest is read with explicit UTF-8.
#
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File scripts\pack.ps1
param(
    [string]$Configuration = 'Debug',
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$VsInstall = 'E:\Visual Studio 2026\chanpin'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$staging  = Join-Path $Root 'dist\_staging'
$outDir   = Join-Path $Root 'dist'
$buildOut = Join-Path $Root ("src\LocalAIHub\bin\{0}\net472" -f $Configuration)
$coreOut  = Join-Path $Root ("src\LocalAIHub.Core\bin\{0}\net472" -f $Configuration)

Write-Host "== packing VSIX ==" -ForegroundColor Cyan
Write-Host "   configuration : $Configuration"
Write-Host "   build output  : $buildOut"

if (-not (Test-Path (Join-Path $buildOut 'LocalAIHub.dll'))) {
    throw "LocalAIHub.dll not found. Build first: dotnet build src\LocalAIHub\LocalAIHub.csproj"
}
if (-not (Test-Path (Join-Path $coreOut 'LocalAIHub.Core.dll'))) {
    throw "LocalAIHub.Core.dll not found. Build the Core project first."
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ---- 1. assemblies ----
Copy-Item (Join-Path $buildOut 'LocalAIHub.dll') $staging
Copy-Item (Join-Path $coreOut  'LocalAIHub.Core.dll') $staging

# ---- 2. pkgdef, written WITH a UTF-8 BOM (VS may silently ignore it otherwise) ----
$pkgdefSrc  = Join-Path $Root 'src\LocalAIHub\LocalAIHub.pkgdef'
$pkgdefText = [IO.File]::ReadAllText($pkgdefSrc)
$utf8Bom    = New-Object System.Text.UTF8Encoding($true)
$pkgdefDst  = Join-Path $staging 'LocalAIHub.pkgdef'
[IO.File]::WriteAllText($pkgdefDst, $pkgdefText, $utf8Bom)

$bytes  = [IO.File]::ReadAllBytes($pkgdefDst)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
if (-not $hasBom) { throw "pkgdef is missing its UTF-8 BOM; Visual Studio may not read it." }
Write-Host "   pkgdef        : BOM verified" -ForegroundColor Green

# ---- 3. manifest: MUST be named extension.vsixmanifest at the package root ----
$manifestPath = Join-Path $staging 'extension.vsixmanifest'
Copy-Item (Join-Path $Root 'src\LocalAIHub\source.extension.vsixmanifest') $manifestPath

# ---- 3b. validate against Visual Studio's own schema ----
# Visual Studio ships the real 2011-namespace schema at Xml\Schemas\PackageManifestSchema.xsd
# (plus sibling files it imports). NOTE: VSIXManifestSchema.xsd in the same folder is the
# older 2010 namespace and CANNOT validate a modern manifest.
$mainSchema = Join-Path $VsInstall 'Xml\Schemas\PackageManifestSchema.xsd'
if (-not (Test-Path $mainSchema)) {
    Write-Host "   WARNING: $mainSchema not found - skipping schema validation" -ForegroundColor Yellow
} else {
    $schemaSet = New-Object System.Xml.Schema.XmlSchemaSet
    $schemaSet.XmlResolver = New-Object System.Xml.XmlUrlResolver
    # Read targetNamespace from the xsd rather than passing $null: PowerShell coerces
    # $null to an empty string for a [String] parameter, which makes Add() throw.
    $xsdText = [IO.File]::ReadAllText($mainSchema)
    $xsdHead = New-Object System.Xml.XmlDocument
    $xsdHead.LoadXml($xsdText)
    $mainNs = $xsdHead.DocumentElement.targetNamespace
    [void]$schemaSet.Add($mainNs, $mainSchema)
    $schemaSet.Compile()  # returns void, but keep pipeline clean

    $vsettings = New-Object System.Xml.XmlReaderSettings
    $vsettings.ValidationType = [System.Xml.ValidationType]::Schema
    $vsettings.Schemas.Add($schemaSet)
    $verrors = New-Object System.Collections.ArrayList
    $vsettings.add_ValidationEventHandler({ param($s, $e) [void]$verrors.Add($e.Message) })

    $vreader = [System.Xml.XmlReader]::Create($manifestPath, $vsettings)
    try { while ($vreader.Read()) { } } finally { $vreader.Close() }

    if ($verrors.Count -gt 0) {
        Write-Host "   manifest FAILED VSIX schema validation:" -ForegroundColor Red
        $verrors | ForEach-Object { Write-Host ("     - " + $_) -ForegroundColor Red }
        throw "extension.vsixmanifest does not conform to PackageManifestSchema.xsd"
    }
    Write-Host ("   manifest      : VSIX schema OK (" + $schemaSet.Count + " schemas)") -ForegroundColor Green
}

# ---- 3c. extra gate: the Prerequisites section must be absent ----
# Passing schema validation does NOT mean the engine can install it. Measured with
# VSIXInstaller's own engine implementation (see tools\IlProbe):
#   keeping <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" ... />
#   makes the engine throw InvalidExtensionPackageException ("not a valid VSIX package"),
#   while deleting only that element makes it accept the package immediately.
# The shipped PackageManifestSchema.xsd ACCEPTS Prerequisites, so this needs its own check.
[xml]$mfDoc = [IO.File]::ReadAllText($manifestPath, (New-Object System.Text.UTF8Encoding($false)))
$prereq = $mfDoc.PackageManifest.Prerequisites

if ($prereq -and $prereq.Prerequisite) {
    Write-Host "   manifest FAILED: a non-empty Prerequisites section makes VSIXInstaller reject the package" -ForegroundColor Red
    Write-Host "     the schema allows it, but the extension engine does not - remove the section" -ForegroundColor Red
    throw "extension.vsixmanifest has a Prerequisites element, which the VS extension engine rejects"
}
Write-Host "   manifest      : no Prerequisites section (engine-safe)" -ForegroundColor Green

# ---- 4. [Content_Types].xml ----
$contentTypes = @'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="vsixmanifest" ContentType="text/xml" />
  <Default Extension="dll" ContentType="application/octet-stream" />
  <Default Extension="pkgdef" ContentType="text/plain" />
  <Default Extension="json" ContentType="application/json" />
  <Default Extension="txt" ContentType="text/plain" />
  <Default Extension="md" ContentType="text/markdown" />
  <Default Extension="png" ContentType="image/png" />
</Types>
'@
[IO.File]::WriteAllText((Join-Path $staging '[Content_Types].xml'), $contentTypes, (New-Object System.Text.UTF8Encoding($false)))

# ---- 5. read version, emit the vsix ----
# Read the manifest as explicit UTF-8: it contains Chinese text and Get-Content in
# PowerShell 5.1 defaults to the ANSI codepage, which corrupts the XML.
[xml]$manifest = [IO.File]::ReadAllText($manifestPath, (New-Object System.Text.UTF8Encoding($false)))
$version  = $manifest.PackageManifest.Metadata.Identity.Version
$vsixPath = Join-Path $outDir ("LocalAIHub-{0}.vsix" -f $version)
if (Test-Path $vsixPath) { Remove-Item $vsixPath -Force }

[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $vsixPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# ---- 6. self-check: the package must contain all of these ----
$zip = [System.IO.Compression.ZipFile]::OpenRead($vsixPath)
try {
    $names    = @($zip.Entries | ForEach-Object { $_.FullName })
    $required = @('extension.vsixmanifest', '[Content_Types].xml', 'LocalAIHub.dll',
                  'LocalAIHub.Core.dll', 'LocalAIHub.pkgdef')
    $missing  = @($required | Where-Object { $names -notcontains $_ })
    if ($missing.Count -gt 0) { throw ("VSIX incomplete, missing: " + ($missing -join ', ')) }
    Write-Host "   entries       :" -ForegroundColor Green
    $zip.Entries | Sort-Object FullName | ForEach-Object {
        Write-Host ("     {0,-30} {1,10} B" -f $_.FullName, $_.Length)
    }
} finally { $zip.Dispose() }

$size = [math]::Round((Get-Item $vsixPath).Length / 1KB, 1)
Write-Host ""
Write-Host ("packed: {0}  ({1} KB)" -f $vsixPath, $size) -ForegroundColor Green
Write-Host ""
Write-Host "Install (prompts for elevation):" -ForegroundColor Yellow
Write-Host ("  VSIXInstaller.exe /quiet /instanceIds:bdfb6cb1 '{0}'" -f $vsixPath)
Write-Host ""
Write-Host "Uninstall:" -ForegroundColor Yellow
Write-Host "  VSIXInstaller.exe /quiet /uninstall:LocalAIHub.9f1c4a02-7d3e-4b18-9c55-0a7f2e5b81d4"
