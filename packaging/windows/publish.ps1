[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [ValidatePattern('^[0-9A-Za-z.-]+$')]
    [string]$Version = '0.1.0-alpha.2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Clear-BoundedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot,

        [int]$MaximumEntries = 10000,

        [switch]$KeepRoot
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
    $rootPrefix = $resolvedRoot.TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a directory outside its allowed root: $resolvedPath"
    }

    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $directories = New-Object 'System.Collections.Generic.List[string]'
    $files = New-Object 'System.Collections.Generic.List[string]'
    $pending.Push($resolvedPath)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $directories.Add($directory)
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (($directories.Count + $files.Count) -ge $MaximumEntries) {
                throw "Publish cleanup exceeded the $MaximumEntries entry safety limit."
            }

            $entryPath = [System.IO.Path]::GetFullPath($entry.FullName)
            if (-not $entryPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Publish cleanup escaped its allowed root: $entryPath"
            }

            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish cleanup refuses to traverse a reparse point: $entryPath"
            }

            if ($entry.PSIsContainer) {
                $pending.Push($entryPath)
            }
            else {
                $files.Add($entryPath)
            }
        }
    }

    foreach ($file in $files) {
        Remove-Item -LiteralPath $file -Force
    }

    $orderedDirectories = $directories | Sort-Object { $_.Length } -Descending
    foreach ($directory in $orderedDirectories) {
        if ($KeepRoot -and $directory -eq $resolvedPath) {
            continue
        }

        Remove-Item -LiteralPath $directory -Force
    }
}

try {
    $projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $projectFile = Join-Path $projectRoot 'src\AgentCallback\AgentCallback.csproj'
    $pluginRoot = Join-Path $projectRoot 'plugins\agent-callback'
    $outputDirectory = Join-Path (Join-Path $pluginRoot 'bin') $RuntimeIdentifier
    $artifactRoot = Join-Path $projectRoot 'artifacts'
    $packageName = "agent-callback-$Version-$RuntimeIdentifier"
    $packageDirectory = Join-Path $artifactRoot $packageName
    $archivePath = Join-Path $artifactRoot ($packageName + '.zip')
    $dotnet = (Get-Command 'dotnet.exe' -ErrorAction Stop).Source

    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "Agent Callback project was not found: $projectFile"
    }

    foreach ($requiredFile in @(
        (Join-Path $PSScriptRoot 'install.ps1'),
        (Join-Path $PSScriptRoot 'uninstall.ps1'),
        (Join-Path $projectRoot 'LICENSE'),
        (Join-Path $projectRoot 'README.md'),
        (Join-Path $projectRoot 'CHANGELOG.md'),
        (Join-Path $projectRoot 'SECURITY.md'),
        (Join-Path $pluginRoot 'skills\agent-callback\SKILL.md'),
        (Join-Path $pluginRoot 'skills\agent-callback\agents\openai.yaml'),
        (Join-Path $pluginRoot 'skills\agent-callback\references\app-installation.example.json')
    )) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Release input was not found: $requiredFile"
        }
    }

    Clear-BoundedDirectory -Path $outputDirectory -AllowedRoot $pluginRoot
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $arguments = @(
        'publish',
        $projectFile,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $outputDirectory,
        '--nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $executable = Join-Path $outputDirectory 'agent-callback.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable was not created: $executable"
    }

    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    Clear-BoundedDirectory -Path $packageDirectory -AllowedRoot $artifactRoot -KeepRoot
    if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    $packageSkill = Join-Path $packageDirectory 'skill\agent-callback'
    New-Item -ItemType Directory -Path (Join-Path $packageSkill 'agents') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageSkill 'references') -Force | Out-Null
    Copy-Item -LiteralPath $executable -Destination (Join-Path $packageDirectory 'agent-callback.exe')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination (Join-Path $packageDirectory 'install.ps1')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $packageDirectory 'uninstall.ps1')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $packageDirectory 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $packageDirectory 'README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination (Join-Path $packageDirectory 'CHANGELOG.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'SECURITY.md') -Destination (Join-Path $packageDirectory 'SECURITY.md')
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'skills\agent-callback\SKILL.md') -Destination (Join-Path $packageSkill 'SKILL.md')
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'skills\agent-callback\agents\openai.yaml') -Destination (Join-Path $packageSkill 'agents\openai.yaml')
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'skills\agent-callback\references\app-installation.example.json') -Destination (Join-Path $packageSkill 'references\app-installation.example.json')

    $packagedExecutable = Join-Path $packageDirectory 'agent-callback.exe'
    $file = Get-Item -LiteralPath $packagedExecutable
    $hash = Get-FileHash -LiteralPath $packagedExecutable -Algorithm SHA256
    $checksumLine = $hash.Hash.ToLowerInvariant() + '  agent-callback.exe' + [Environment]::NewLine
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $packageDirectory 'SHA256SUMS.txt'),
        $checksumLine,
        $utf8WithoutBom)
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $archive = Get-Item -LiteralPath $archivePath
    $archiveHash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    $archiveChecksumPath = $archivePath + '.sha256'
    $archiveChecksumLine = $archiveHash.Hash.ToLowerInvariant() + '  ' + $archive.Name + [Environment]::NewLine
    [System.IO.File]::WriteAllText(
        $archiveChecksumPath,
        $archiveChecksumLine,
        $utf8WithoutBom)
    [pscustomobject]@{
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        executable = $file.FullName
        sizeBytes = $file.Length
        sha256 = $hash.Hash.ToLowerInvariant()
        packageDirectory = $packageDirectory
        archive = $archive.FullName
        archiveSizeBytes = $archive.Length
        archiveSha256 = $archiveHash.Hash.ToLowerInvariant()
        archiveChecksum = $archiveChecksumPath
    } | ConvertTo-Json -Depth 3
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
