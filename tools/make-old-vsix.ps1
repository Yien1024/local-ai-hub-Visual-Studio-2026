# 造一个"旧版"VSIX（复现当初被 VSIXInstaller 拒绝的那份 manifest），
# 用来把"到底哪一处让引擎拒绝"从猜测变成可复现的 A/B 实验。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-old-vsix.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot   # vs-local-ai
$staging = Join-Path $root 'dist\_staging_old'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

$src = Join-Path $root 'dist\_staging'
Copy-Item (Join-Path $src 'LocalAIHub.dll') $staging
Copy-Item (Join-Path $src 'LocalAIHub.Core.dll') $staging
Copy-Item (Join-Path $src 'LocalAIHub.pkgdef') $staging
Copy-Item (Join-Path $src '[Content_Types].xml') $staging

# 旧版 manifest：带 <Installation> 里的注释 + d:Source="File" + design 命名空间声明
$old = @'
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"
                 xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Id="LocalAIHub.9f1c4a02-7d3e-4b18-9c55-0a7f2e5b81d4" Version="1.0.0" Language="zh-CN" Publisher="LocalAIHub" />
    <DisplayName>Local AI Hub (local models + web search)</DisplayName>
    <Description xml:space="preserve">reproduction of the rejected manifest</Description>
  </Metadata>
  <Installation InstalledByMsi="false">
    <!-- this comment was inside Installation in the rejected build -->
    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[18.0,19.0)">
      <ProductArchitecture>amd64</ProductArchitecture>
    </InstallationTarget>
  </Installation>
  <Dependencies>
    <Dependency Id="Microsoft.Framework.NDP" DisplayName="Microsoft .NET Framework" Version="[4.7.2,)" />
  </Dependencies>
  <Prerequisites>
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[18.0,19.0)" DisplayName="Visual Studio core editor" />
  </Prerequisites>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.VsPackage" d:Source="File" Path="LocalAIHub.pkgdef" />
  </Assets>
</PackageManifest>
'@
[IO.File]::WriteAllText((Join-Path $staging 'extension.vsixmanifest'), $old, (New-Object System.Text.UTF8Encoding($false)))

$out = Join-Path $root 'dist\_old-repro.vsix'
if (Test-Path $out) { Remove-Item $out -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $out, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "已生成复现包: $out"
