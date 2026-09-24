[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Configuration = 'Release',
    [switch]$KeepArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$script:RepositoryUrl = 'https://github.com/0ehsan-sh0/Ehsan.Webhook.Kit'
$script:PackageVersion = '1.0.0'
$script:TargetFrameworks = @('net8.0', 'net9.0', 'net10.0')
$script:SourceLinkRepositoryPath = '0ehsan-sh0/Ehsan.Webhook.Kit'
$script:SourceLinkGuid = [Guid]'CC110556-A091-4D38-9FEC-25AB9A351A6A'
$script:Errors = New-Object -TypeName 'System.Collections.Generic.List[string]'
$script:PackageOutput = $null
$script:ArtifactsRoot = $null
$script:InspectionDirectory = $null
$script:TempRoot = $null
$script:ExpectedPackages = @(
    [PSCustomObject]@{
        Id = 'WebhookKit.Abstractions'
        Project = 'src\WebhookKit.Abstractions\WebhookKit.Abstractions.csproj'
        Assembly = 'WebhookKit.Abstractions'
        Dependencies = @{}
        FrameworkReferences = @{}
    }
    [PSCustomObject]@{
        Id = 'WebhookKit.Core'
        Project = 'src\WebhookKit.Core\WebhookKit.Core.csproj'
        Assembly = 'WebhookKit.Core'
        Dependencies = @{
            'WebhookKit.Abstractions' = '1.0.0'
            'Microsoft.Extensions.DependencyInjection.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Hosting.Abstractions' = '8.0.1'
            'Microsoft.Extensions.Logging.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Options' = '8.0.2'
        }
        FrameworkReferences = @{}
    }
    [PSCustomObject]@{
        Id = 'WebhookKit.AspNetCore'
        Project = 'src\WebhookKit.AspNetCore\WebhookKit.AspNetCore.csproj'
        Assembly = 'WebhookKit.AspNetCore'
        Dependencies = @{
            'WebhookKit.Core' = '1.0.0'
        }
        FrameworkReferences = @{
            'Microsoft.AspNetCore.App' = $true
        }
    }
    [PSCustomObject]@{
        Id = 'WebhookKit.EntityFrameworkCore'
        Project = 'src\WebhookKit.EntityFrameworkCore\WebhookKit.EntityFrameworkCore.csproj'
        Assembly = 'WebhookKit.EntityFrameworkCore'
        Dependencies = @{
            'WebhookKit.Abstractions' = '1.0.0'
            'Microsoft.EntityFrameworkCore' = '8.0.11'
            'Microsoft.EntityFrameworkCore.Relational' = '8.0.11'
            'Microsoft.Extensions.DependencyInjection' = '8.0.1'
            'Microsoft.Extensions.DependencyInjection.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Logging.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Options' = '8.0.2'
        }
        FrameworkReferences = @{}
    }
    [PSCustomObject]@{
        Id = 'WebhookKit.Redis'
        Project = 'src\WebhookKit.Redis\WebhookKit.Redis.csproj'
        Assembly = 'WebhookKit.Redis'
        Dependencies = @{
            'WebhookKit.Abstractions' = '1.0.0'
            'Microsoft.Extensions.DependencyInjection' = '8.0.1'
            'Microsoft.Extensions.DependencyInjection.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Logging.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Options' = '8.0.2'
            'StackExchange.Redis' = '2.8.24'
        }
        FrameworkReferences = @{}
    }
    [PSCustomObject]@{
        Id = 'WebhookKit.Testing'
        Project = 'src\WebhookKit.Testing\WebhookKit.Testing.csproj'
        Assembly = 'WebhookKit.Testing'
        Dependencies = @{
            'WebhookKit.Core' = '1.0.0'
            'Microsoft.AspNetCore.Mvc.Testing' = '8.0.11'
            'Microsoft.Extensions.DependencyInjection' = '8.0.1'
            'Microsoft.Extensions.DependencyInjection.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Hosting.Abstractions' = '8.0.1'
            'Microsoft.Extensions.Logging.Abstractions' = '8.0.2'
            'Microsoft.Extensions.Options' = '8.0.2'
            'Microsoft.Extensions.Options.ConfigurationExtensions' = '8.0.0'
        }
        FrameworkReferences = @{}
    }
)

function ConvertTo-SafeText {
    param(
        [AllowNull()]
        [string]$Text
    )

    if ($null -eq $Text) {
        return ''
    }

    $safe = $Text
    $safe = [Regex]::Replace($safe, '(?i)(password|pwd|secret|token|api[-_ ]?key|connectionstring)\s*([:=])\s*[^;\r\n,]+', '$1$2[REDACTED]')
    $safe = [Regex]::Replace($safe, '(?i)(server|data source|host|port|database|user id|username)\s*=\s*[^;\r\n]+', '$1=[REDACTED]')
    $safe = [Regex]::Replace($safe, '(?i)(https?://)[^/\s:@]+:[^@/\s]+@', '$1[REDACTED]@')
    return $safe
}

function Add-ValidationError {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    [void]$script:Errors.Add((ConvertTo-SafeText $Message))
}

function Get-ExceptionText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Exception]$Exception
    )

    if ([string]::IsNullOrWhiteSpace($Exception.Message)) {
        return $Exception.GetType().Name
    }

    return ConvertTo-SafeText $Exception.Message
}

function Get-UniqueSortedText {
    param(
        [AllowNull()]
        [string[]]$Values
    )

    if ($null -eq $Values) {
        return @()
    }

    $copy = [string[]]@($Values | Sort-Object -Unique)
    return ,$copy
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ChildPath,
        [Parameter(Mandatory = $true)]
        [string]$ParentPath
    )

    $child = [IO.Path]::GetFullPath($ChildPath).TrimEnd('\', '/')
    $parent = [IO.Path]::GetFullPath($ParentPath).TrimEnd('\', '/')
    $prefix = $parent + [IO.Path]::DirectorySeparatorChar
    return $child.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-ReparsePoint {
    param(
        [Parameter(Mandatory = $true)]
        [IO.FileSystemInfo]$Item
    )

    return (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq [IO.FileAttributes]::ReparsePoint)
}

function Assert-NoReparsePointsInTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $children = @(Get-ChildItem -LiteralPath $Path -Force)
    foreach ($child in $children) {
        if (Test-ReparsePoint $child) {
            throw 'The generated package output contains a reparse point.'
        }

        if ($child.PSIsContainer) {
            Assert-NoReparsePointsInTree $child.FullName
        }
    }
}

function Assert-SafeGeneratedPaths {
    if ([string]::IsNullOrWhiteSpace($script:PackageOutput) -or [string]::IsNullOrWhiteSpace($script:ArtifactsRoot)) {
        throw 'Generated package paths have not been initialized.'
    }

    $repoRoot = [IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path -Path $repoRoot -ChildPath 'artifacts'))
    $packageOutput = [IO.Path]::GetFullPath((Join-Path -Path $artifactsRoot -ChildPath 'packages'))
    if (-not $script:ArtifactsRoot.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The artifacts root is not the repository artifacts directory.'
    }

    if (-not $packageOutput.Equals($script:PackageOutput, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The package output path is not the generated artifacts/packages directory.'
    }

    if (-not (Test-PathWithin -ChildPath $packageOutput -ParentPath $artifactsRoot)) {
        throw 'The package output path is outside the repository artifacts directory.'
    }

    $repoItem = Get-Item -LiteralPath $repoRoot -Force
    if (-not $repoItem.PSIsContainer -or (Test-ReparsePoint $repoItem)) {
        throw 'The repository root is not a safe directory.'
    }

    if (Test-Path -LiteralPath $artifactsRoot) {
        $artifactsItem = Get-Item -LiteralPath $artifactsRoot -Force
        if (-not $artifactsItem.PSIsContainer -or (Test-ReparsePoint $artifactsItem)) {
            throw 'The repository artifacts path is not a safe directory.'
        }
    }

    if (Test-Path -LiteralPath $packageOutput) {
        $outputItem = Get-Item -LiteralPath $packageOutput -Force
        if (-not $outputItem.PSIsContainer -or (Test-ReparsePoint $outputItem)) {
            throw 'The generated package output path is not a safe directory.'
        }

        Assert-NoReparsePointsInTree $packageOutput
    }
}

function Remove-GeneratedPackageDirectory {
    Assert-SafeGeneratedPaths
    if (-not (Test-Path -LiteralPath $script:PackageOutput)) {
        return
    }

    $outputItem = Get-Item -LiteralPath $script:PackageOutput -Force
    if (-not $outputItem.PSIsContainer) {
        throw 'The generated package output is not a directory.'
    }

    Remove-Item -LiteralPath $script:PackageOutput -Recurse -Force
}

function Prepare-GeneratedPackageDirectory {
    Assert-SafeGeneratedPaths
    Remove-GeneratedPackageDirectory
    if (-not (Test-Path -LiteralPath $script:ArtifactsRoot)) {
        New-Item -ItemType Directory -Path $script:ArtifactsRoot -Force | Out-Null
    }

    New-Item -ItemType Directory -Path $script:PackageOutput -Force | Out-Null
    Assert-SafeGeneratedPaths
}

function Initialize-InspectionDirectory {
    $script:TempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $script:TempRoot -PathType Container)) {
        throw 'The system temporary directory is unavailable.'
    }

    $script:InspectionDirectory = Join-Path -Path $script:TempRoot -ChildPath ('webhookkit-package-inspection-' + [Guid]::NewGuid().ToString('N'))
    if (-not (Test-PathWithin -ChildPath $script:InspectionDirectory -ParentPath $script:TempRoot)) {
        throw 'The package inspection directory is outside the system temporary directory.'
    }

    if (-not $script:InspectionDirectory.StartsWith((Join-Path -Path $script:TempRoot -ChildPath 'webhookkit-package-inspection-'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The package inspection directory has an unsafe name.'
    }

    New-Item -ItemType Directory -Path $script:InspectionDirectory -Force | Out-Null
}

function Remove-InspectionDirectory {
    if ([string]::IsNullOrWhiteSpace($script:InspectionDirectory) -or [string]::IsNullOrWhiteSpace($script:TempRoot)) {
        return
    }

    if (-not (Test-PathWithin -ChildPath $script:InspectionDirectory -ParentPath $script:TempRoot)) {
        Add-ValidationError 'Refused to remove an inspection directory outside the system temporary directory.'
        return
    }

    $expectedPrefix = Join-Path -Path $script:TempRoot -ChildPath 'webhookkit-package-inspection-'
    if (-not $script:InspectionDirectory.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Add-ValidationError 'Refused to remove an inspection directory with an unsafe name.'
        return
    }

    if (Test-Path -LiteralPath $script:InspectionDirectory) {
        $inspectionItem = Get-Item -LiteralPath $script:InspectionDirectory -Force
        if (-not $inspectionItem.PSIsContainer -or (Test-ReparsePoint $inspectionItem)) {
            Add-ValidationError 'Refused to remove an unsafe inspection directory.'
            return
        }

        Remove-Item -LiteralPath $script:InspectionDirectory -Recurse -Force
    }
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$Operation
    )

    $global:LASTEXITCODE = 0
    try {
        & $FilePath @Arguments 2>&1 | Out-Null
        $exitCode = $global:LASTEXITCODE
    }
    catch {
        throw ('Unable to execute ' + $Operation + '.')
    }

    if ($exitCode -ne 0) {
        throw ($Operation + ' failed with exit code ' + $exitCode + '.')
    }
}

function Get-ZipEntryBytes {
    param(
        [Parameter(Mandatory = $true)]
        $Entry
    )

    $stream = $Entry.Open()
    $memory = New-Object -TypeName 'System.IO.MemoryStream'
    try {
        $stream.CopyTo($memory)
        return ,$memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $stream.Dispose()
    }
}

function Get-ZipEntryText {
    param(
        [Parameter(Mandatory = $true)]
        $Entry
    )

    $bytes = Get-ZipEntryBytes $Entry
    if ($bytes.Length -gt 4194304) {
        throw 'The package metadata entry is unexpectedly large.'
    }

    $text = [Text.Encoding]::UTF8.GetString($bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
        $text = $text.Substring(1)
    }

    return $text
}

function Get-ZipEntry {
    param(
        [AllowNull()]
        [object[]]$Entries,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $matches = @($Entries | Where-Object { $_.FullName -ieq $Name })
    if ($matches.Count -gt 1) {
        Add-ValidationError ('Package contains duplicate ZIP entry ' + $Name + '.')
        return $null
    }

    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Get-RequiredElementText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlNode]$Parent,
        [Parameter(Mandatory = $true)]
        [string]$LocalName,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $nodes = @($Parent.SelectNodes("./*[local-name()='$LocalName']"))
    if ($nodes.Count -ne 1) {
        Add-ValidationError ($Label + ' must contain exactly one ' + $LocalName + ' element.')
        return $null
    }

    $value = $nodes[0].InnerText.Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        Add-ValidationError ($Label + ' has an empty ' + $LocalName + ' value.')
        return $null
    }

    return $value
}

function Read-NuspecDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $settings = New-Object -TypeName 'System.Xml.XmlReaderSettings'
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $stringReader = New-Object -TypeName 'System.IO.StringReader' -ArgumentList $Text
    $xmlReader = [System.Xml.XmlReader]::Create($stringReader, $settings)
    $document = New-Object -TypeName 'System.Xml.XmlDocument'
    $document.XmlResolver = $null
    try {
        $document.Load($xmlReader)
    }
    finally {
        $xmlReader.Dispose()
        $stringReader.Dispose()
    }

    if ($null -eq $document.DocumentElement -or $document.DocumentElement.LocalName -ne 'package' -or $document.DocumentElement.NamespaceURI -notlike 'http://schemas.microsoft.com/packaging/*') {
        throw 'The nuspec root is not a supported package document.'
    }

    return $document
}

function Get-NuspecMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlDocument]$Document,
        [Parameter(Mandatory = $true)]
        $ExpectedPackage
    )

    $metadataNodes = @($Document.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']"))
    if ($metadataNodes.Count -ne 1) {
        Add-ValidationError ($ExpectedPackage.Id + ' nuspec must contain one metadata element.')
        return $null
    }

    $metadata = $metadataNodes[0]
    $id = Get-RequiredElementText $metadata 'id' $ExpectedPackage.Id
    $version = Get-RequiredElementText $metadata 'version' $ExpectedPackage.Id
    $authors = Get-RequiredElementText $metadata 'authors' $ExpectedPackage.Id
    $description = Get-RequiredElementText $metadata 'description' $ExpectedPackage.Id
    $tags = Get-RequiredElementText $metadata 'tags' $ExpectedPackage.Id
    $readme = Get-RequiredElementText $metadata 'readme' $ExpectedPackage.Id
    $projectUrl = Get-RequiredElementText $metadata 'projectUrl' $ExpectedPackage.Id

    $licenseNodes = @($metadata.SelectNodes("./*[local-name()='license']"))
    $licenseText = $null
    if ($licenseNodes.Count -ne 1) {
        Add-ValidationError ($ExpectedPackage.Id + ' nuspec must contain exactly one license element.')
    }
    else {
        $licenseText = $licenseNodes[0].InnerText.Trim()
        if ($licenseNodes[0].GetAttribute('type') -ine 'expression' -or $licenseText -ine 'MIT') {
            Add-ValidationError ($ExpectedPackage.Id + ' must use the MIT license expression.')
        }
    }

    $repositoryNodes = @($metadata.SelectNodes("./*[local-name()='repository']"))
    if ($repositoryNodes.Count -ne 1) {
        Add-ValidationError ($ExpectedPackage.Id + ' nuspec must contain exactly one repository element.')
    }
    else {
        $repositoryType = $repositoryNodes[0].GetAttribute('type')
        $repositoryUrl = $repositoryNodes[0].GetAttribute('url')
        if ($repositoryType -ine 'git' -or $repositoryUrl -ine $script:RepositoryUrl) {
            Add-ValidationError ($ExpectedPackage.Id + ' repository metadata is invalid.')
        }
    }

    if ($null -ne $id -and $id -ine $ExpectedPackage.Id) {
        Add-ValidationError ($ExpectedPackage.Id + ' nuspec ID does not match its expected package ID.')
    }

    if ($null -ne $version -and ($version -ine $script:PackageVersion -or $version -match '[-+]')) {
        Add-ValidationError ($ExpectedPackage.Id + ' must use version 1.0.0 without a prerelease suffix.')
    }

    if ($null -ne $readme -and $readme -ine 'README.md') {
        Add-ValidationError ($ExpectedPackage.Id + ' must declare README.md as its readme.')
    }

    if ($null -ne $projectUrl -and $projectUrl -ine $script:RepositoryUrl) {
        Add-ValidationError ($ExpectedPackage.Id + ' project metadata is invalid.')
    }

    $dependencyGroups = @()
    $dependencyGroupNodes = @($metadata.SelectNodes("./*[local-name()='dependencies']/*[local-name()='group']"))
    foreach ($groupNode in $dependencyGroupNodes) {
        $targetFramework = $groupNode.GetAttribute('targetFramework')
        $dependencies = @()
        $dependencyNodes = @($groupNode.SelectNodes("./*[local-name()='dependency']"))
        foreach ($dependencyNode in $dependencyNodes) {
            $dependencies += [PSCustomObject]@{
                Id = $dependencyNode.GetAttribute('id')
                Version = $dependencyNode.GetAttribute('version')
                Exclude = $dependencyNode.GetAttribute('exclude')
                Include = $dependencyNode.GetAttribute('include')
                PrivateAssets = $dependencyNode.GetAttribute('privateAssets')
                DevelopmentDependency = $dependencyNode.GetAttribute('developmentDependency')
            }
        }

        $dependencyGroups += [PSCustomObject]@{
            TargetFramework = $targetFramework
            Dependencies = $dependencies
        }
    }

    $frameworkGroups = @()
    $frameworkGroupNodes = @($metadata.SelectNodes("./*[local-name()='frameworkReferences']/*[local-name()='group']"))
    foreach ($groupNode in $frameworkGroupNodes) {
        $frameworkReferences = @()
        $frameworkNodes = @($groupNode.SelectNodes("./*[local-name()='frameworkReference']"))
        foreach ($frameworkNode in $frameworkNodes) {
            $frameworkReferences += $frameworkNode.GetAttribute('name')
        }

        $frameworkGroups += [PSCustomObject]@{
            TargetFramework = $groupNode.GetAttribute('targetFramework')
            FrameworkReferences = $frameworkReferences
        }
    }

    return [PSCustomObject]@{
        Id = $id
        Version = $version
        Authors = $authors
        Description = $description
        Tags = $tags
        Readme = $readme
        License = $licenseText
        ProjectUrl = $projectUrl
        DependencyGroups = $dependencyGroups
        FrameworkGroups = $frameworkGroups
    }
}

function Test-ForbiddenDependencyId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DependencyId,
        [Parameter(Mandatory = $true)]
        [string]$PackageId
    )

    $lower = $DependencyId.ToLowerInvariant()
    if ($lower -eq 'microsoft.sourcelink.github' -or $lower -like 'microsoft.sourcelink.*' -or $lower -like 'microsoft.build.tasks.git*') {
        return 'SourceLink or build-only dependency'
    }

    $forbidden = @(
        'microsoft.net.test.sdk',
        'xunit',
        'xunit.runner.visualstudio',
        'fluentassertions',
        'nsubstitute',
        'coverlet.collector',
        'benchmarkdotnet',
        'microsoft.testplatform.testhost',
        'microsoft.testplatform.objectmodel',
        'nunit',
        'moq'
    )
    if ($forbidden -contains $lower -or $lower -like 'xunit.*' -or $lower -like 'fluentassertions.*' -or $lower -like 'nsubstitute.*' -or $lower -like 'coverlet.*' -or $lower -like 'benchmarkdotnet.*' -or $lower -like 'microsoft.net.test.sdk.*') {
        return 'test or benchmark dependency'
    }

    if ($lower -eq 'microsoft.aspnetcore.mvc.testing' -and $PackageId -ine 'WebhookKit.Testing') {
        return 'ASP.NET MVC Testing dependency outside WebhookKit.Testing'
    }

    return $null
}

function Test-Dependencies {
    param(
        [Parameter(Mandatory = $true)]
        $ExpectedPackage,
        [Parameter(Mandatory = $true)]
        $Metadata
    )

    $groupsByFramework = @{}
    foreach ($group in @($Metadata.DependencyGroups)) {
        $framework = [string]$group.TargetFramework
        if ([string]::IsNullOrWhiteSpace($framework)) {
            Add-ValidationError ($ExpectedPackage.Id + ' has a dependency group without a target framework.')
            continue
        }

        if ($groupsByFramework.ContainsKey($framework)) {
            Add-ValidationError ($ExpectedPackage.Id + ' has duplicate dependency groups for ' + $framework + '.')
            continue
        }

        $groupsByFramework[$framework] = $group.Dependencies
    }

    foreach ($framework in $script:TargetFrameworks) {
        if (-not $groupsByFramework.ContainsKey($framework)) {
            Add-ValidationError ($ExpectedPackage.Id + ' is missing the dependency group for ' + $framework + '.')
            continue
        }

        $actualDependencies = @($groupsByFramework[$framework])
        $seen = @{}
        foreach ($dependency in $actualDependencies) {
            $dependencyId = [string]$dependency.Id
            if ([string]::IsNullOrWhiteSpace($dependencyId)) {
                Add-ValidationError ($ExpectedPackage.Id + ' has a dependency without an ID for ' + $framework + '.')
                continue
            }

            if ($seen.ContainsKey($dependencyId)) {
                Add-ValidationError ($ExpectedPackage.Id + ' has duplicate dependency ' + $dependencyId + ' for ' + $framework + '.')
                continue
            }

            $seen[$dependencyId] = $true
            $forbiddenReason = Test-ForbiddenDependencyId $dependencyId $ExpectedPackage.Id
            if ($null -ne $forbiddenReason) {
                Add-ValidationError ($ExpectedPackage.Id + ' contains forbidden dependency ' + $dependencyId + ': ' + $forbiddenReason + '.')
            }

            if ($dependency.DevelopmentDependency -ieq 'true') {
                Add-ValidationError ($ExpectedPackage.Id + ' contains development-only dependency ' + $dependencyId + '.')
            }

            if (-not $ExpectedPackage.Dependencies.ContainsKey($dependencyId)) {
                Add-ValidationError ($ExpectedPackage.Id + ' has unexpected dependency ' + $dependencyId + ' for ' + $framework + '.')
                continue
            }

            $expectedVersion = [string]$ExpectedPackage.Dependencies[$dependencyId]
            if ($dependency.Version -ine $expectedVersion) {
                Add-ValidationError ($ExpectedPackage.Id + ' dependency ' + $dependencyId + ' must use version ' + $expectedVersion + '.')
            }
        }

        foreach ($dependencyId in @($ExpectedPackage.Dependencies.Keys)) {
            if (-not $seen.ContainsKey($dependencyId)) {
                Add-ValidationError ($ExpectedPackage.Id + ' is missing dependency ' + $dependencyId + ' for ' + $framework + '.')
            }
        }
    }

    foreach ($framework in @($groupsByFramework.Keys)) {
        if ($script:TargetFrameworks -notcontains $framework) {
            Add-ValidationError ($ExpectedPackage.Id + ' has unexpected dependency target framework ' + $framework + '.')
        }
    }
}

function Test-FrameworkReferences {
    param(
        [Parameter(Mandatory = $true)]
        $ExpectedPackage,
        [Parameter(Mandatory = $true)]
        $Metadata
    )

    $groupsByFramework = @{}
    foreach ($group in @($Metadata.FrameworkGroups)) {
        $framework = [string]$group.TargetFramework
        if ([string]::IsNullOrWhiteSpace($framework)) {
            Add-ValidationError ($ExpectedPackage.Id + ' has a framework reference group without a target framework.')
            continue
        }

        if ($groupsByFramework.ContainsKey($framework)) {
            Add-ValidationError ($ExpectedPackage.Id + ' has duplicate framework reference groups for ' + $framework + '.')
            continue
        }

        $groupsByFramework[$framework] = @($group.FrameworkReferences)
    }

    foreach ($framework in $script:TargetFrameworks) {
        if (-not $groupsByFramework.ContainsKey($framework)) {
            if ($ExpectedPackage.FrameworkReferences.Count -gt 0) {
                Add-ValidationError ($ExpectedPackage.Id + ' is missing framework references for ' + $framework + '.')
            }
            continue
        }

        $actual = @($groupsByFramework[$framework])
        $seen = @{}
        foreach ($reference in $actual) {
            $name = [string]$reference
            if ($seen.ContainsKey($name)) {
                Add-ValidationError ($ExpectedPackage.Id + ' has duplicate framework reference ' + $name + '.')
                continue
            }

            $seen[$name] = $true
            if (-not $ExpectedPackage.FrameworkReferences.ContainsKey($name)) {
                Add-ValidationError ($ExpectedPackage.Id + ' has unexpected framework reference ' + $name + '.')
            }
        }

        foreach ($name in @($ExpectedPackage.FrameworkReferences.Keys)) {
            if (-not $seen.ContainsKey($name)) {
                Add-ValidationError ($ExpectedPackage.Id + ' is missing framework reference ' + $name + '.')
            }
        }
    }

    foreach ($framework in @($groupsByFramework.Keys)) {
        if ($script:TargetFrameworks -notcontains $framework) {
            Add-ValidationError ($ExpectedPackage.Id + ' has unexpected framework reference target ' + $framework + '.')
        }
    }
}

function Get-ForbiddenEntryReason {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [switch]$AllowPdb
    )

    $normalized = $Name.Replace('\', '/')
    $lower = $normalized.ToLowerInvariant()
    $parts = @($lower -split '/')
    $file = $parts[$parts.Count - 1]
    $extension = [IO.Path]::GetExtension($file)

    if ($Name -match '\\' -or $Name -match '[\x00-\x1F]' -or $normalized.StartsWith('/') -or $normalized -match '^[A-Za-z]:' -or $normalized -match '(^|/)\.\.(/|$)') {
        return 'unsafe ZIP path'
    }

    $blockedSegments = @('tests', 'test', 'samples', 'sample', 'benchmarks', 'benchmark', '.git', '.github')
    foreach ($part in $parts) {
        if ($blockedSegments -contains $part -or $part -like '.env*') {
            return 'source, test, sample, benchmark, environment, or repository metadata path'
        }
    }

    $blockedExtensions = @('.cs', '.csproj', '.fs', '.fsproj', '.vb', '.vbproj', '.props', '.targets', '.sln', '.slnx', '.c', '.h', '.hpp', '.cpp', '.razor', '.cshtml', '.vbhtml', '.config', '.user', '.suo', '.cache', '.log', '.bak', '.mdb', '.mif', '.pch', '.pfx', '.p12', '.pem', '.key', '.crt', '.cer', '.jks', '.keystore', '.kdbx', '.zip', '.7z', '.rar', '.tar', '.gz', '.tgz', '.bz2', '.xz', '.jar', '.nupkg', '.snupkg')
    if ($blockedExtensions -contains $extension) {
        return 'forbidden file extension'
    }

    if ($file -match '^\.env' -or $file -match '^\.sln' -or $file -match '(?i)^secrets?\.json$' -or $file -match '(?i)^appsettings.*\.json$' -or $file -match '(?i)^(credentials?|connectionstrings?|privatekeys?|secrets?)\.(json|xml|yml|yaml)$' -or $file -match '(?i)(credential|password|passwd|secret|token|connectionstring|privatekey|apikey)' -or $file -match '(?i)\.publishsettings$') {
        return 'secret or credential-like file name'
    }

    if (-not $AllowPdb -and $extension -eq '.pdb') {
        return 'PDB in a nupkg'
    }

    return $null
}

function Test-ZipEntryNames {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Entries,
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedNames,
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [switch]$AllowPdb
    )

    $actualNames = @($Entries | ForEach-Object { [string]$_.FullName })
    foreach ($entry in $Entries) {
        $reason = Get-ForbiddenEntryReason -Name ([string]$entry.FullName) -AllowPdb:$AllowPdb
        if ($null -ne $reason) {
            Add-ValidationError ($Label + ' contains forbidden entry ' + $entry.FullName + ': ' + $reason + '.')
        }
    }

    foreach ($expectedName in (Get-UniqueSortedText $ExpectedNames)) {
        $matches = @($actualNames | Where-Object { $_ -ieq $expectedName })
        if ($matches.Count -eq 0) {
            Add-ValidationError ($Label + ' is missing required entry ' + $expectedName + '.')
        }
        elseif ($matches.Count -gt 1) {
            Add-ValidationError ($Label + ' has duplicate required entry ' + $expectedName + '.')
        }
    }

    foreach ($actualName in (Get-UniqueSortedText $actualNames)) {
        $allowed = @($ExpectedNames | Where-Object { $_ -ieq $actualName })
        if ($allowed.Count -eq 0) {
            Add-ValidationError ($Label + ' contains unexpected entry ' + $actualName + '.')
        }
    }
}

function Get-NupkgExpectedEntries {
    param(
        [Parameter(Mandatory = $true)]
        $ExpectedPackage
    )

    $entries = @(
        '[Content_Types].xml',
        '_rels/.rels',
        'package/services/metadata/core-properties/nuget.psmdcp',
        'README.md',
        ($ExpectedPackage.Id + '.nuspec')
    )
    foreach ($framework in $script:TargetFrameworks) {
        $entries += ('lib/' + $framework + '/' + $ExpectedPackage.Assembly + '.dll')
        $entries += ('lib/' + $framework + '/' + $ExpectedPackage.Assembly + '.xml')
    }

    return ,$entries
}

function Get-SnupkgExpectedEntries {
    param(
        [Parameter(Mandatory = $true)]
        $ExpectedPackage
    )

    $entries = @(
        '[Content_Types].xml',
        '_rels/.rels',
        'package/services/metadata/core-properties/nuget.psmdcp',
        ($ExpectedPackage.Id + '.nuspec')
    )
    foreach ($framework in $script:TargetFrameworks) {
        $entries += ('lib/' + $framework + '/' + $ExpectedPackage.Assembly + '.pdb')
    }

    return ,$entries
}

function Test-ByteSequence {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Haystack,
        [Parameter(Mandatory = $true)]
        [byte[]]$Needle
    )

    if ($Needle.Length -eq 0 -or $Haystack.Length -lt $Needle.Length) {
        return $Needle.Length -eq 0
    }

    for ($index = 0; $index -le $Haystack.Length - $Needle.Length; $index++) {
        $matched = $true
        for ($offset = 0; $offset -lt $Needle.Length; $offset++) {
            if ($Haystack[$index + $offset] -ne $Needle[$offset]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            return $true
        }
    }

    return $false
}

function Test-PortablePdbSourceLink {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Bytes
    )

    if ($Bytes.Length -lt 4 -or $Bytes[0] -ne 0x42 -or $Bytes[1] -ne 0x53 -or $Bytes[2] -ne 0x4A -or $Bytes[3] -ne 0x42) {
        Add-ValidationError 'A symbol entry is not a portable PDB.'
        return $false
    }

    if (-not (Test-ByteSequence -Haystack $Bytes -Needle $script:SourceLinkGuid.ToByteArray())) {
        Add-ValidationError 'A portable PDB does not contain the SourceLink custom-debug record.'
        return $false
    }

    $text = [Text.Encoding]::UTF8.GetString($Bytes)
    $match = [Regex]::Match($text, '\{"documents":\s*\{.*?\}\}', [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $match.Success) {
        Add-ValidationError 'A portable PDB does not contain a SourceLink documents map.'
        return $false
    }

    $sourceLink = $null
    try {
        $sourceLink = $match.Value | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Add-ValidationError 'A portable PDB contains invalid SourceLink JSON.'
        return $false
    }

    $documentsProperty = $sourceLink.PSObject.Properties['documents']
    if ($null -eq $documentsProperty -or $null -eq $documentsProperty.Value) {
        Add-ValidationError 'A portable PDB has no SourceLink document mappings.'
        return $false
    }

    $documents = @($documentsProperty.Value.PSObject.Properties)
    if ($documents.Count -eq 0) {
        Add-ValidationError 'A portable PDB has an empty SourceLink document map.'
        return $false
    }

    $expectedPattern = '^https://raw\.githubusercontent\.com/' + [Regex]::Escape($script:SourceLinkRepositoryPath) + '/[0-9A-Fa-f]{40}/\*$'
    foreach ($document in $documents) {
        $url = $null
        if ($document.Value -is [string]) {
            $url = [string]$document.Value
        }
        elseif ($null -ne $document.Value) {
            $rawProperty = $document.Value.PSObject.Properties['rawBaseUrl']
            if ($null -ne $rawProperty) {
                $url = [string]$rawProperty.Value
            }
        }

        if ($url -notmatch $expectedPattern) {
            Add-ValidationError 'A portable PDB SourceLink mapping does not target the configured GitHub repository.'
            return $false
        }
    }

    return $true
}

function Read-NupkgArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        $ExpectedPackage
    )

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries)
        $names = @($entries | ForEach-Object { [string]$_.FullName })
        $metadata = $null
        $nuspecName = $ExpectedPackage.Id + '.nuspec'
        $nuspecEntry = Get-ZipEntry -Entries $entries -Name $nuspecName
        if ($null -eq $nuspecEntry) {
            Add-ValidationError ($ExpectedPackage.Id + ' nupkg is missing ' + $nuspecName + '.')
        }
        else {
            try {
                $document = Read-NuspecDocument (Get-ZipEntryText $nuspecEntry)
                $metadata = Get-NuspecMetadata $document $ExpectedPackage
            }
            catch {
                Add-ValidationError ($ExpectedPackage.Id + ' has an invalid nuspec document.')
            }
        }

        return [PSCustomObject]@{
            Names = $names
            Metadata = $metadata
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Test-Nupkg {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        $ExpectedPackage
    )

    $archiveInfo = $null
    try {
        $archiveInfo = Read-NupkgArchive $Path $ExpectedPackage
    }
    catch {
        Add-ValidationError ($ExpectedPackage.Id + ' nupkg could not be opened as a ZIP archive.')
        return $null
    }

    $expectedEntries = Get-NupkgExpectedEntries $ExpectedPackage
    $archiveEntries = @($archiveInfo.Names | ForEach-Object { [PSCustomObject]@{ FullName = $_ } })
    Test-ZipEntryNames -Entries $archiveEntries -ExpectedNames $expectedEntries -Label ($ExpectedPackage.Id + ' nupkg')
    if ($null -ne $archiveInfo.Metadata) {
        Test-Dependencies $ExpectedPackage $archiveInfo.Metadata
        Test-FrameworkReferences $ExpectedPackage $archiveInfo.Metadata
    }

    return $archiveInfo
}

function Test-Snupkg {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        $ExpectedPackage
    )

    $archive = $null
    $names = @()
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $entries = @($archive.Entries)
        $names = @($entries | ForEach-Object { [string]$_.FullName })
        $expectedEntries = Get-SnupkgExpectedEntries $ExpectedPackage
        Test-ZipEntryNames -Entries $entries -ExpectedNames $expectedEntries -Label ($ExpectedPackage.Id + ' snupkg') -AllowPdb

        $index = 0
        foreach ($framework in $script:TargetFrameworks) {
            $pdbName = 'lib/' + $framework + '/' + $ExpectedPackage.Assembly + '.pdb'
            $pdbEntry = Get-ZipEntry -Entries $entries -Name $pdbName
            if ($null -eq $pdbEntry) {
                continue
            }

            $index++
            $pdbBytes = $null
            try {
                $pdbBytes = Get-ZipEntryBytes $pdbEntry
                $pdbPath = Join-Path -Path $script:InspectionDirectory -ChildPath ('pdb-' + $index + '-' + $ExpectedPackage.Assembly + '.pdb')
                [IO.File]::WriteAllBytes($pdbPath, $pdbBytes)
                $pdbBytes = [IO.File]::ReadAllBytes($pdbPath)
            }
            catch {
                Add-ValidationError ($ExpectedPackage.Id + ' snupkg could not provide a readable ' + $framework + ' PDB.')
                continue
            }

            [void](Test-PortablePdbSourceLink $pdbBytes)
        }
    }
    catch {
        Add-ValidationError ($ExpectedPackage.Id + ' snupkg could not be opened as a ZIP archive.')
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }

    return $names
}

function Test-PackagePairContent {
    param(
        [Parameter(Mandatory = $true)]
        $ExpectedPackage,
        [AllowNull()]
        $NupkgInfo,
        [AllowNull()]
        $SnupkgNames
    )

    if ($null -eq $NupkgInfo -or $null -eq $SnupkgNames) {
        return
    }

    $allowedShared = @(
        '[Content_Types].xml',
        '_rels/.rels',
        'package/services/metadata/core-properties/nuget.psmdcp',
        ($ExpectedPackage.Id + '.nuspec')
    )
    $nupkgNames = @($NupkgInfo.Names)
    $snupkgNames = @($SnupkgNames)
    foreach ($name in $snupkgNames) {
        if ($nupkgNames -contains $name -and $allowedShared -notcontains $name) {
            Add-ValidationError ($ExpectedPackage.Id + ' snupkg duplicates nupkg content entry ' + $name + '.')
        }
    }
}

function Test-PackageSet {
    if (-not (Test-Path -LiteralPath $script:PackageOutput -PathType Container)) {
        Add-ValidationError 'The generated artifacts/packages directory is missing.'
        return
    }

    $files = @(Get-ChildItem -LiteralPath $script:PackageOutput -Force -File)
    $nupkgFiles = @($files | Where-Object { $_.Extension -ieq '.nupkg' })
    $snupkgFiles = @($files | Where-Object { $_.Extension -ieq '.snupkg' })
    $expectedNupkgNames = @($script:ExpectedPackages | ForEach-Object { $_.Id + '.' + $script:PackageVersion + '.nupkg' })
    $expectedSnupkgNames = @($script:ExpectedPackages | ForEach-Object { $_.Id + '.' + $script:PackageVersion + '.snupkg' })

    foreach ($file in $nupkgFiles) {
        if ($expectedNupkgNames -notcontains $file.Name) {
            Add-ValidationError ('Unexpected package file ' + $file.Name + '.')
        }
    }

    foreach ($file in $snupkgFiles) {
        if ($expectedSnupkgNames -notcontains $file.Name) {
            Add-ValidationError ('Unexpected symbol package file ' + $file.Name + '.')
        }
    }

    foreach ($expectedPackage in $script:ExpectedPackages) {
        $nupkgName = $expectedPackage.Id + '.' + $script:PackageVersion + '.nupkg'
        $snupkgName = $expectedPackage.Id + '.' + $script:PackageVersion + '.snupkg'
        $nupkgMatches = @($nupkgFiles | Where-Object { $_.Name -ieq $nupkgName })
        $snupkgMatches = @($snupkgFiles | Where-Object { $_.Name -ieq $snupkgName })

        $nupkgInfo = $null
        $snupkgNames = $null
        if ($nupkgMatches.Count -eq 0) {
            Add-ValidationError ('Missing package ' + $nupkgName + '.')
        }
        elseif ($nupkgMatches.Count -gt 1) {
            Add-ValidationError ('Expected exactly one package file for ' + $nupkgName + '.')
        }
        else {
            $nupkgInfo = Test-Nupkg $nupkgMatches[0].FullName $expectedPackage
        }

        if ($snupkgMatches.Count -eq 0) {
            Add-ValidationError ('Missing symbol package ' + $snupkgName + '.')
        }
        elseif ($snupkgMatches.Count -gt 1) {
            Add-ValidationError ('Expected exactly one symbol package file for ' + $snupkgName + '.')
        }
        else {
            $snupkgNames = Test-Snupkg $snupkgMatches[0].FullName $expectedPackage
        }

        Test-PackagePairContent $expectedPackage $nupkgInfo $snupkgNames
    }
}

function Invoke-PackProjects {
    $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        Add-ValidationError 'The dotnet executable is required for package packing.'
        return
    }

    foreach ($expectedPackage in $script:ExpectedPackages) {
        $arguments = @(
            'pack',
            (Join-Path -Path (Join-Path -Path $PSScriptRoot -ChildPath '..') -ChildPath $expectedPackage.Project),
            '--configuration',
            $Configuration,
            '--no-restore',
            '--output',
            $script:PackageOutput,
            '--include-symbols',
            '--verbosity',
            'quiet',
            '-p:PackageVersion=1.0.0',
            '-p:Deterministic=true',
            '-p:ContinuousIntegrationBuild=true',
            '-p:DebugType=portable',
            '-p:IncludeSymbols=true',
            '-p:SymbolPackageFormat=snupkg',
            '-p:PublishRepositoryUrl=true',
            '-p:EmbedUntrackedSources=true'
        )

        try {
            Invoke-ExternalCommand -FilePath $dotnet.Source -Arguments $arguments -Operation ('Packing ' + $expectedPackage.Id)
            Write-Output ('Packed ' + $expectedPackage.Id + '.')
        }
        catch {
            Add-ValidationError (Get-ExceptionText $_.Exception)
        }
    }
}

try {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
    $script:ArtifactsRoot = [IO.Path]::GetFullPath((Join-Path -Path $repoRoot -ChildPath 'artifacts'))
    $script:PackageOutput = [IO.Path]::GetFullPath((Join-Path -Path $script:ArtifactsRoot -ChildPath 'packages'))
    Assert-SafeGeneratedPaths
    Prepare-GeneratedPackageDirectory
    Initialize-InspectionDirectory
    Invoke-PackProjects
    Test-PackageSet
}
catch {
    Add-ValidationError ('Validation aborted: ' + (Get-ExceptionText $_.Exception))
}
finally {
    if ($null -ne $script:InspectionDirectory) {
        try {
            Remove-InspectionDirectory
        }
        catch {
            Add-ValidationError 'The controlled package inspection directory could not be cleaned.'
        }
    }

    if (-not $KeepArtifacts) {
        try {
            Remove-GeneratedPackageDirectory
        }
        catch {
            Add-ValidationError 'The generated artifacts/packages directory could not be cleaned safely.'
        }
    }
    elseif ($null -ne $script:PackageOutput -and (Test-Path -LiteralPath $script:PackageOutput -PathType Container)) {
        Write-Output 'Artifacts retained at artifacts/packages.'
    }
}

$uniqueErrors = Get-UniqueSortedText $script:Errors.ToArray()
if ($uniqueErrors.Count -gt 0) {
    foreach ($validationError in $uniqueErrors) {
        Write-Output ('ERROR: ' + $validationError)
    }

    Write-Output ('Package validation failed with ' + $uniqueErrors.Count + ' error(s).')
    exit 1
}

Write-Output 'Package validation passed: 6 packages inspected.'
exit 0
