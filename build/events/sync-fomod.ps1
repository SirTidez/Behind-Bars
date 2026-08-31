param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [string]$IsMono = "",

    [string]$Configuration = ""
)

$ErrorActionPreference = "Stop"

function Ensure-Directory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Get-ConfigurationRuntimeAssembly {
    param(
        [string]$BinRoot,
        [string]$ConfigurationName,
        [string]$Filter
    )

    if ([string]::IsNullOrWhiteSpace($ConfigurationName)) {
        return $null
    }

    $configurationRoot = Join-Path $BinRoot $ConfigurationName
    if (-not (Test-Path -LiteralPath $configurationRoot)) {
        return $null
    }

    return Get-ChildItem -Path $configurationRoot -Recurse -Filter $Filter -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Get-RuntimeConfigurationName {
    param(
        [string]$CurrentConfiguration,
        [bool]$BuildIsMono,
        [string]$RuntimeName
    )

    if ($RuntimeName -eq "Mono") {
        if ($BuildIsMono) {
            return $CurrentConfiguration
        }

        if ($CurrentConfiguration -match "IL2CPP") {
            return ($CurrentConfiguration -ireplace "IL2CPP", "Mono")
        }
    }

    if ($RuntimeName -eq "IL2CPP") {
        if (-not $BuildIsMono) {
            return $CurrentConfiguration
        }

        if ($CurrentConfiguration -match "Mono") {
            return ($CurrentConfiguration -ireplace "Mono", "IL2CPP")
        }
    }

    return $null
}

function Sync-RuntimeAssembly {
    param(
        [string]$RuntimeName,
        [string]$AssemblyFilter,
        [string]$PreferredAssemblyPath,
        [string]$RuntimeConfiguration,
        [string]$BinRoot,
        [string]$FomodRoot
    )

    $runtimeModsDir = Join-Path $FomodRoot ("data\Runtime\{0}\Mods" -f $RuntimeName)
    Ensure-Directory -Path $runtimeModsDir

    $sourcePath = $null
    if ($PreferredAssemblyPath -and (Test-Path -LiteralPath $PreferredAssemblyPath)) {
        $sourcePath = $PreferredAssemblyPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($RuntimeConfiguration)) {
        $candidate = Get-ConfigurationRuntimeAssembly -BinRoot $BinRoot -ConfigurationName $RuntimeConfiguration -Filter $AssemblyFilter
        if ($candidate) {
            $sourcePath = $candidate.FullName
        }
    }

    if (-not $sourcePath) {
        throw ("Missing {0} assembly for configuration '{1}'. Build '{1}' before creating the FOMOD package." -f $RuntimeName, $RuntimeConfiguration)
    }

    Get-ChildItem -Path $runtimeModsDir -File -Filter "*.dll" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $destinationPath = Join-Path $runtimeModsDir (Split-Path -Path $sourcePath -Leaf)
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
    Write-Host ("Synced {0} assembly from '{1}': {2}" -f $RuntimeName, $RuntimeConfiguration, $destinationPath)

    return Get-ChildItem -Path $runtimeModsDir -File -Filter "*.dll" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

$projectRootPath = (Resolve-Path -LiteralPath $ProjectRoot).Path
$targetAssemblyPath = (Resolve-Path -LiteralPath $TargetPath).Path

$projectVersionPath = Join-Path $projectRootPath "project_version.json"
$fomodRootPath = Join-Path $projectRootPath "bin\FOMod\Mods\Behind Bars"
$fomodMetaPath = Join-Path $fomodRootPath "fomod"
$infoPath = Join-Path $fomodMetaPath "Info.xml"
$moduleConfigPath = Join-Path $fomodMetaPath "ModuleConfig.xml"
$binRootPath = Join-Path $projectRootPath "bin"

Ensure-Directory -Path $fomodMetaPath

$versionJson = Get-Content -LiteralPath $projectVersionPath -Raw | ConvertFrom-Json
$version = [string]$versionJson.version
$description = [string]$versionJson.description

if (Test-Path -LiteralPath $infoPath) {
    [xml]$infoXml = Get-Content -LiteralPath $infoPath
}
else {
    [xml]$infoXml = @"
<fomod>
  <Name>Behind Bars</Name>
  <Author>SirTidez</Author>
  <Version></Version>
  <Description></Description>
</fomod>
"@
}

$infoXml.fomod.Version = $version
$infoXml.fomod.Description = $description
$infoXml.Save($infoPath)
Write-Host "Updated FOMOD Info.xml"

$isMonoBuild = $IsMono -eq "true"
$resolvedConfiguration = $Configuration
if ([string]::IsNullOrWhiteSpace($resolvedConfiguration)) {
    $targetFrameworkDirectory = Split-Path -Path $targetAssemblyPath -Parent
    $configurationDirectory = Split-Path -Path $targetFrameworkDirectory -Parent
    $resolvedConfiguration = Split-Path -Path $configurationDirectory -Leaf
}

$monoConfiguration = Get-RuntimeConfigurationName -CurrentConfiguration $resolvedConfiguration -BuildIsMono $isMonoBuild -RuntimeName "Mono"
$il2cppConfiguration = Get-RuntimeConfigurationName -CurrentConfiguration $resolvedConfiguration -BuildIsMono $isMonoBuild -RuntimeName "IL2CPP"

if ([string]::IsNullOrWhiteSpace($monoConfiguration) -or [string]::IsNullOrWhiteSpace($il2cppConfiguration)) {
    throw ("Unable to infer Mono/IL2CPP configuration pairing from '{0}'." -f $resolvedConfiguration)
}

$monoPreferredPath = if ($isMonoBuild) { $targetAssemblyPath } else { $null }
$il2cppPreferredPath = if (-not $isMonoBuild) { $targetAssemblyPath } else { $null }

$monoAssembly = Sync-RuntimeAssembly -RuntimeName "Mono" -AssemblyFilter "*-Mono.dll" -PreferredAssemblyPath $monoPreferredPath -RuntimeConfiguration $monoConfiguration -BinRoot $binRootPath -FomodRoot $fomodRootPath
$il2cppAssembly = Sync-RuntimeAssembly -RuntimeName "IL2CPP" -AssemblyFilter "*-IL2CPP.dll" -PreferredAssemblyPath $il2cppPreferredPath -RuntimeConfiguration $il2cppConfiguration -BinRoot $binRootPath -FomodRoot $fomodRootPath

$legacyDataPath = Join-Path $fomodRootPath "data\Mods"
if (Test-Path -LiteralPath $legacyDataPath) {
    Remove-Item -LiteralPath $legacyDataPath -Recurse -Force
    Write-Host "Removed legacy single-runtime data\\Mods folder"
}

$plugins = @()

if ($il2cppAssembly) {
    $plugins += @{
        Name = "Behind Bars - IL2CPP"
        Description = "Install the IL2CPP build for current Schedule I versions."
        Source = "data\Runtime\IL2CPP\Mods"
    }
}

if ($monoAssembly) {
    $plugins += @{
        Name = "Behind Bars - Mono"
        Description = "Install the Mono build for legacy/alternate Schedule I Mono setups."
        Source = "data\Runtime\Mono\Mods"
    }
}

if ($plugins.Count -eq 0) {
    throw "No runtime assemblies were found for FOMOD packaging."
}

$groupType = if ($plugins.Count -gt 1) { "SelectExactlyOne" } else { "SelectAtLeastOne" }
$runtimeStepName = "Runtime - Main/Beta: IL2CPP, Alternate/Alternate Beta: Mono"

$pluginXmlLines = foreach ($plugin in $plugins) {
    $name = [System.Security.SecurityElement]::Escape([string]$plugin.Name)
    $pluginDescription = [System.Security.SecurityElement]::Escape([string]$plugin.Description)
    $source = [System.Security.SecurityElement]::Escape([string]$plugin.Source)

@"
                        <plugin name="$name">
                            <description>$pluginDescription</description>
                            <files>
                                <folder source="$source" destination="Mods" alwaysInstall="false" installIfUsable="true" priority="0" />
                            </files>
                            <typeDescriptor>
                                <type name="Required" />
                            </typeDescriptor>
                        </plugin>
"@
}

$moduleImageLine = ""
if (Test-Path -LiteralPath $moduleConfigPath) {
    [xml]$existingModuleConfig = Get-Content -LiteralPath $moduleConfigPath
    if ($existingModuleConfig.config.moduleImage -and $existingModuleConfig.config.moduleImage.path) {
        $imagePath = [string]$existingModuleConfig.config.moduleImage.path
        $resolvedImagePath = Join-Path $fomodRootPath $imagePath
        if (Test-Path -LiteralPath $resolvedImagePath) {
            $escapedImagePath = [System.Security.SecurityElement]::Escape($imagePath)
            $moduleImageLine = "`t<moduleImage path=`"$escapedImagePath`"/>`r`n"
        }
    }
}

$moduleConfigXml = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Auto-generated during build by sync-fomod.ps1 -->
<config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
`t<moduleName>Behind Bars</moduleName>
$moduleImageLine`t<installSteps order="Explicit">
`t`t<installStep name="$runtimeStepName">
`t`t`t<optionalFileGroups>
`t`t`t`t<group name="Runtime" type="$groupType">
`t`t`t`t`t<plugins>
$($pluginXmlLines -join "`r`n")
`t`t`t`t`t</plugins>
`t`t`t`t</group>
`t`t`t</optionalFileGroups>
`t`t</installStep>
`t</installSteps>
</config>
"@

Set-Content -LiteralPath $moduleConfigPath -Value $moduleConfigXml -Encoding UTF8
Write-Host "Updated FOMOD ModuleConfig.xml"

$installerZipPath = Join-Path $binRootPath ("Behind-Bars-Nexus-Installer-{0}.zip" -f $version)
$archivePaths = @(
    (Join-Path $fomodRootPath "fomod"),
    (Join-Path $fomodRootPath "data")
)

foreach ($archivePath in $archivePaths) {
    if (-not (Test-Path -LiteralPath $archivePath)) {
        throw ("Cannot create installer zip because path does not exist: {0}" -f $archivePath)
    }
}

if (Test-Path -LiteralPath $installerZipPath) {
    Remove-Item -LiteralPath $installerZipPath -Force
}

Compress-Archive -Path $archivePaths -DestinationPath $installerZipPath -CompressionLevel Optimal
Write-Host ("Created FOMOD installer zip: {0}" -f $installerZipPath)
