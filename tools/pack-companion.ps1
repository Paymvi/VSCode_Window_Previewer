# Packs companion/ into a .vsix without needing vsce or network access. A .vsix is a zip with a manifest.
param([string]$Out = (Join-Path $PSScriptRoot '..\companion\project-previews-companion.vsix'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$src = Join-Path $PSScriptRoot '..\companion'
$pkg = Get-Content (Join-Path $src 'package.json') -Raw | ConvertFrom-Json

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Language="en-US" Id="$($pkg.name)" Version="$($pkg.version)" Publisher="$($pkg.publisher)" />
    <DisplayName>$($pkg.displayName)</DisplayName>
    <Description xml:space="preserve">$([Security.SecurityElement]::Escape($pkg.description))</Description>
    <Tags></Tags>
    <Categories>Other</Categories>
    <GalleryFlags>Public</GalleryFlags>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$($pkg.engines.vscode)" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionDependencies" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionPack" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="ui" />
      <Property Id="Microsoft.VisualStudio.Code.LocalizedLanguages" Value="" />
    </Properties>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
  </Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Details" Path="extension/README.md" Addressable="true" />
  </Assets>
</PackageManifest>
"@
$types = @"
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension=".json" ContentType="application/json" /><Default Extension=".js" ContentType="application/javascript" /><Default Extension=".md" ContentType="text/markdown" /><Default Extension=".vsixmanifest" ContentType="text/xml" /></Types>
"@

if (Test-Path $Out) { Remove-Item $Out }
$zip = [System.IO.Compression.ZipFile]::Open($Out, 'Create')
function Add([string]$name, [string]$text) {
    $e = $zip.CreateEntry($name)  # forward slashes: VS Code requires them
    $w = New-Object System.IO.StreamWriter($e.Open(), (New-Object System.Text.UTF8Encoding $false))
    $w.Write($text); $w.Close()
}
Add 'extension.vsixmanifest' $manifest
Add '[Content_Types].xml' $types
foreach ($f in 'package.json', 'extension.js', 'README.md') { Add "extension/$f" (Get-Content (Join-Path $src $f) -Raw) }
$zip.Dispose()
"packed $Out"
