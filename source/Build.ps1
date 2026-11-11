param(
    [Parameter(Mandatory)][string]$RevitRoot,
    [Parameter(Mandatory)][string]$FamilyTemplateRoot,
    [string]$WorkspaceRoot = $PSScriptRoot,
    [ValidatePattern('^[A-Za-z0-9_.]+$')][string]$AssemblyName = 'PileCage.CircularColumn2025.R26'
)
$ErrorActionPreference = 'Stop'
$revit = (Resolve-Path -LiteralPath $RevitRoot).Path
$templates = (Resolve-Path -LiteralPath $FamilyTemplateRoot).Path.TrimEnd('\')
foreach ($name in @('RevitAPI.dll', 'RevitAPIUI.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $revit $name) -PathType Leaf)) {
        throw "Revit 2025 API file missing: $name"
    }
}
$workspace = [System.IO.Path]::GetFullPath($WorkspaceRoot)
$stage = Join-Path $workspace 'build/R26'
$binary = Join-Path $workspace 'bin/R26'
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $binary)) {
    throw 'Choose a workspace without existing R26 build directories.'
}
$projectFolder = Join-Path $stage 'src/PileCage.Revit2025'
$null = New-Item -ItemType Directory -Path $projectFolder -Force
foreach ($relativePath in @('pilecage.workspace', 'config/rules.json')) {
    $target = Join-Path $workspace $relativePath
    if (-not (Test-Path -LiteralPath $target)) {
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relativePath) -Destination $target
    }
}
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src/PileCage.Revit2025') -File | Copy-Item -Destination $projectFolder
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets') -Destination (Join-Path $stage 'assets') -Recurse
foreach ($file in Get-ChildItem -LiteralPath $projectFolder -Filter '*.cs' -File) {
    $old = [System.IO.File]::ReadAllText($file.FullName)
    $new = $old.Replace('__REVIT_METRIC_FAMILY_TEMPLATES__', $templates)
    if ($new -ne $old) { [System.IO.File]::WriteAllText($file.FullName, $new) }
}
& dotnet build (Join-Path $projectFolder 'PileCage.Revit2025.csproj') -c Release --nologo `
    "-p:RevitRoot=$revit" "-p:AssemblyName=$AssemblyName" '-p:Version=2.0.0' -o $binary
if ($LASTEXITCODE -ne 0) { throw 'R26 build failed.' }
$dll = Join-Path $binary ($AssemblyName + '.dll')
$escaped = [System.Security.SecurityElement]::Escape($dll)
$addin = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Rebar Modeling</Name>
    <Assembly>$escaped</Assembly>
    <AddInId>FEE870D5-70DB-4DAF-983A-5C2B68BFD101</AddInId>
    <FullClassName>PileCage.App</FullClassName>
    <VendorId>PCV1</VendorId>
    <VendorDescription>Pile reinforcement for Revit 2025</VendorDescription>
  </AddIn>
  <AddIn Type="Command">
    <Text>钢筋笼参数建模</Text>
    <Assembly>$escaped</Assembly>
    <AddInId>FBA5FC43-9A1F-4984-9B33-F3BD15C7782D</AddInId>
    <FullClassName>PileCage.ShowWindowCommand</FullClassName>
    <VendorId>PCV1</VendorId>
    <VendorDescription>Pile reinforcement for Revit 2025</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText((Join-Path $binary 'PileCage.Reproduction.addin'), $addin)
Write-Output "Built $dll"
