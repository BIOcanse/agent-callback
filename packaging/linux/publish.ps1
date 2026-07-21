[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier = 'linux-x64',

    [ValidatePattern('^[0-9A-Za-z.-]+$')]
    [string]$Version = '0.1.0-alpha.4'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    $projectFile = Join-Path $projectRoot 'src\AgentCallback\AgentCallback.csproj'
    $pluginRoot = Join-Path $projectRoot 'plugins\agent-callback'
    $artifactRoot = Join-Path $projectRoot 'artifacts'
    $packageName = "agent-callback-$Version-$RuntimeIdentifier"
    $packageDirectory = Join-Path $artifactRoot $packageName
    $archivePath = Join-Path $artifactRoot ($packageName + '.tar.gz')
    $publishDirectory = Join-Path $artifactRoot ("publish-$RuntimeIdentifier")

    foreach ($path in @($packageDirectory, $publishDirectory)) {
        if (Test-Path -LiteralPath $path) {
            $resolved = [System.IO.Path]::GetFullPath($path)
            $prefix = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd('\') + '\'
            if (-not $resolved.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to clean outside the artifact root: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }

    dotnet publish $projectFile --configuration Release --framework net10.0 `
        --runtime $RuntimeIdentifier --self-contained true --output $publishDirectory `
        --nologo -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $publishedBinary = Join-Path $publishDirectory 'agent-callback'
    if (-not (Test-Path -LiteralPath $publishedBinary -PathType Leaf)) {
        throw "Published Linux executable was not created: $publishedBinary"
    }

    $packageSkill = Join-Path $packageDirectory 'skill\agent-callback'
    New-Item -ItemType Directory -Path (Join-Path $packageSkill 'agents') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageSkill 'references') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'opencode') -Force | Out-Null
    Copy-Item -LiteralPath $publishedBinary -Destination (Join-Path $packageDirectory 'agent-callback')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.sh') -Destination (Join-Path $packageDirectory 'install.sh')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.sh') -Destination (Join-Path $packageDirectory 'uninstall.sh')
    foreach ($file in @('LICENSE', 'README.md', 'CHANGELOG.md', 'SECURITY.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination (Join-Path $packageDirectory $file)
    }
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'skills\agent-callback\SKILL.md') -Destination (Join-Path $packageSkill 'SKILL.md')
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'skills\agent-callback\agents\openai.yaml') -Destination (Join-Path $packageSkill 'agents\openai.yaml')
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'skills\agent-callback\references\app-installation.example.json') -Destination (Join-Path $packageSkill 'references\app-installation.example.json')
    Copy-Item -LiteralPath (Join-Path $pluginRoot 'integrations\opencode\agent-callback.js') -Destination (Join-Path $packageDirectory 'opencode\agent-callback.js')

    $binaryHash = (Get-FileHash -LiteralPath (Join-Path $packageDirectory 'agent-callback') -Algorithm SHA256).Hash.ToLowerInvariant()
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        (Join-Path $packageDirectory 'SHA256SUMS.txt'),
        "$binaryHash  agent-callback`n",
        $utf8WithoutBom)

    if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    $resolvedPackagePath = [System.IO.Path]::GetFullPath($packageDirectory)
    $resolvedArchivePath = [System.IO.Path]::GetFullPath($archivePath)
    if ($resolvedPackagePath -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "Linux package path is not on a WSL-mounted local drive: $resolvedPackagePath"
    }
    $packageWslPath = '/mnt/' + $Matches[1].ToLowerInvariant() + '/' +
        $Matches[2].Replace('\', '/')
    if ($resolvedArchivePath -notmatch '^([A-Za-z]):\\(.*)$') {
        throw "Linux archive path is not on a WSL-mounted local drive: $resolvedArchivePath"
    }
    $archiveWslPath = '/mnt/' + $Matches[1].ToLowerInvariant() + '/' +
        $Matches[2].Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($packageWslPath) -or
        [string]::IsNullOrWhiteSpace($archiveWslPath)) {
        throw 'WSL could not resolve the Linux release paths.'
    }

    $archiveScript = @"
set -euo pipefail
source_dir='$packageWslPath'
archive_path='$archiveWslPath'
package_name='$packageName'
work_dir=`$(mktemp -d /tmp/agent-callback-publish.XXXXXX)
case "`$work_dir" in /tmp/agent-callback-publish.*) ;; *) exit 1;; esac
mkdir -p "`$work_dir/`$package_name"
cp -a "`$source_dir/." "`$work_dir/`$package_name/"
find "`$work_dir/`$package_name" -type d -exec chmod 0755 {} +
find "`$work_dir/`$package_name" -type f -exec chmod 0644 {} +
chmod 0755 "`$work_dir/`$package_name/agent-callback" \
  "`$work_dir/`$package_name/install.sh" \
  "`$work_dir/`$package_name/uninstall.sh"
tar -czf "`$archive_path" -C "`$work_dir" "`$package_name"
rm -rf -- "`$work_dir"
"@
    $encodedScript = [Convert]::ToBase64String(
        [System.Text.Encoding]::UTF8.GetBytes($archiveScript))
    & wsl.exe -- bash -lc "echo $encodedScript | base64 -d | bash"
    if ($LASTEXITCODE -ne 0) { throw "WSL tar failed with exit code $LASTEXITCODE." }
    $archive = Get-Item -LiteralPath $archivePath
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $archivePath + '.sha256',
        "$archiveHash  $($archive.Name)`n",
        $utf8WithoutBom)

    [pscustomobject]@{
        version = $Version
        runtimeIdentifier = $RuntimeIdentifier
        executable = (Join-Path $packageDirectory 'agent-callback')
        sha256 = $binaryHash
        archive = $archive.FullName
        archiveSizeBytes = $archive.Length
        archiveSha256 = $archiveHash
    } | ConvertTo-Json -Depth 3
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
