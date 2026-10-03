[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\AgentCallback'),
    [string]$SkillDirectory = (Join-Path $env:USERPROFILE '.codex\skills\agent-callback'),
    [string]$OpenCodeSkillDirectory = (Join-Path $env:USERPROFILE '.config\opencode\skills\agent-callback'),
    [string]$OpenCodePluginDirectory = (Join-Path $env:USERPROFILE '.config\opencode\plugins'),
    [string]$Version = '0.1.0-alpha.5',
    [switch]$DoNotStartHost
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedRoot = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must remain under $AllowedRoot. Received: $resolvedPath"
    }

    return $resolvedPath
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $json = $Value | ConvertTo-Json -Depth 8
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $utf8WithoutBom)
}

try {
    $sourceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
    $sourceExecutable = Join-Path $sourceRoot 'agent-callback.exe'
    $sourceUninstaller = Join-Path $sourceRoot 'uninstall.ps1'
    $sourceLicense = Join-Path $sourceRoot 'LICENSE'
    $sourceReadme = Join-Path $sourceRoot 'README.md'
    $sourceChecksums = Join-Path $sourceRoot 'SHA256SUMS.txt'
    $sourceSkill = Join-Path $sourceRoot 'skill\agent-callback'
    $sourceOpenCodePlugin = Join-Path $sourceRoot 'opencode\agent-callback.js'
    foreach ($requiredFile in @(
        $sourceExecutable,
        $sourceUninstaller,
        $sourceLicense,
        $sourceReadme,
        $sourceChecksums,
        $sourceOpenCodePlugin,
        (Join-Path $sourceSkill 'SKILL.md'),
        (Join-Path $sourceSkill 'agents\openai.yaml')
    )) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Release package is incomplete: $requiredFile"
        }
    }

    $checksumLine = (Get-Content -LiteralPath $sourceChecksums -Raw).Trim()
    if ($checksumLine -notmatch '^([0-9a-fA-F]{64})\s{2}agent-callback\.exe$') {
        throw 'SHA256SUMS.txt has an invalid format.'
    }

    $expectedHash = $Matches[1].ToLowerInvariant()
    $actualHash = (Get-FileHash -LiteralPath $sourceExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'Release package executable checksum verification failed.'
    }

    $programsRoot = Join-Path $env:LOCALAPPDATA 'Programs'
    $codexSkillsRoot = Join-Path $env:USERPROFILE '.codex\skills'
    $openCodeConfigRoot = Join-Path $env:USERPROFILE '.config\opencode'
    $resolvedInstall = Assert-ChildPath -Path $InstallDirectory -AllowedRoot $programsRoot -Description 'Install directory'
    $resolvedSkill = Assert-ChildPath -Path $SkillDirectory -AllowedRoot $codexSkillsRoot -Description 'Skill directory'
    $resolvedOpenCodeSkill = Assert-ChildPath -Path $OpenCodeSkillDirectory -AllowedRoot $openCodeConfigRoot -Description 'OpenCode skill directory'
    $resolvedOpenCodePluginDirectory = Assert-ChildPath -Path $OpenCodePluginDirectory -AllowedRoot $openCodeConfigRoot -Description 'OpenCode plugin directory'
    $dataDirectory = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AgentCallback'))
    $installedExecutable = Join-Path $resolvedInstall 'agent-callback.exe'
    $installedUninstaller = Join-Path $resolvedInstall 'uninstall.ps1'
    $installedState = Join-Path $resolvedInstall 'install-state.json'
    $skillReferences = Join-Path $resolvedSkill 'references'
    $skillInstallation = Join-Path $skillReferences 'app-installation.json'
    $openCodeSkillReferences = Join-Path $resolvedOpenCodeSkill 'references'
    $openCodeSkillInstallation = Join-Path $openCodeSkillReferences 'app-installation.json'
    $installedOpenCodePlugin = Join-Path $resolvedOpenCodePluginDirectory 'agent-callback.js'

    if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
        & $installedExecutable host stop 2>$null | Out-Null
        Start-Sleep -Milliseconds 300
    }

    New-Item -ItemType Directory -Path $resolvedInstall -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $resolvedSkill 'agents') -Force | Out-Null
    New-Item -ItemType Directory -Path $skillReferences -Force | Out-Null
    New-Item -ItemType Directory -Path $openCodeSkillReferences -Force | Out-Null
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null

    Copy-Item -LiteralPath $sourceExecutable -Destination $installedExecutable -Force
    Copy-Item -LiteralPath $sourceUninstaller -Destination $installedUninstaller -Force
    Copy-Item -LiteralPath $sourceLicense -Destination (Join-Path $resolvedInstall 'LICENSE') -Force
    Copy-Item -LiteralPath $sourceReadme -Destination (Join-Path $resolvedInstall 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $sourceSkill 'SKILL.md') -Destination (Join-Path $resolvedSkill 'SKILL.md') -Force
    Copy-Item -LiteralPath (Join-Path $sourceSkill 'agents\openai.yaml') -Destination (Join-Path $resolvedSkill 'agents\openai.yaml') -Force
    Copy-Item -LiteralPath (Join-Path $sourceSkill 'SKILL.md') -Destination (Join-Path $resolvedOpenCodeSkill 'SKILL.md') -Force
    $examplePath = Join-Path $sourceSkill 'references\app-installation.example.json'
    if (Test-Path -LiteralPath $examplePath -PathType Leaf) {
        Copy-Item -LiteralPath $examplePath -Destination (Join-Path $skillReferences 'app-installation.example.json') -Force
        Copy-Item -LiteralPath $examplePath -Destination (Join-Path $openCodeSkillReferences 'app-installation.example.json') -Force
    }

    $ownerPath = Join-Path $dataDirectory 'install-owner.json'
    $installId = $null
    $dataOwned = $false
    if (Test-Path -LiteralPath $ownerPath -PathType Leaf) {
        $existingOwner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
        if ($existingOwner.schemaVersion -eq 1 -and
            -not [string]::IsNullOrWhiteSpace($existingOwner.installId)) {
            $installId = $existingOwner.installId
            $dataOwned = $true
        }
        else {
            throw 'Existing data ownership marker is invalid; installation will not overwrite it.'
        }
    }
    else {
        $existingDataEntries = @(Get-ChildItem -LiteralPath $dataDirectory -Force)
        $installId = [Guid]::NewGuid().ToString()
        $dataOwned = $existingDataEntries.Count -eq 0
    }

    $installIdJson = $installId | ConvertTo-Json -Compress
    $pluginOwnerMarker = 'const managedInstallId = ' + $installIdJson
    if (Test-Path -LiteralPath $installedOpenCodePlugin -PathType Leaf) {
        $existingPlugin = Get-Content -LiteralPath $installedOpenCodePlugin -Raw
        if ($existingPlugin.IndexOf(
            $pluginOwnerMarker,
            [System.StringComparison]::Ordinal) -lt 0) {
            throw "Existing OpenCode plugin is not owned by this Agent Callback installation: $installedOpenCodePlugin"
        }
    }

    New-Item -ItemType Directory -Path $resolvedOpenCodePluginDirectory -Force | Out-Null
    $pluginTemplate = Get-Content -LiteralPath $sourceOpenCodePlugin -Raw
    $executableJson = $installedExecutable | ConvertTo-Json -Compress
    $pluginContent = $pluginTemplate.Replace(
        '__AGENT_CALLBACK_EXECUTABLE_JSON__',
        $executableJson).Replace(
        '__AGENT_CALLBACK_INSTALL_ID_JSON__',
        $installIdJson)
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $installedOpenCodePlugin,
        $pluginContent,
        $utf8WithoutBom)

    $powerShell = (Get-Command 'powershell.exe' -ErrorAction Stop).Source
    $uninstallCommand = '"' + $powerShell + '" -NoProfile -ExecutionPolicy Bypass -File "' + $installedUninstaller + '"'
    $state = [ordered]@{
        schemaVersion = 1
        installId = $installId
        displayName = 'Agent Callback'
        version = $Version
        executablePath = $installedExecutable
        installDirectory = $resolvedInstall
        dataDirectory = $dataDirectory
        dataOwned = $dataOwned
        skillDirectory = $resolvedSkill
        openCodeSkillDirectory = $resolvedOpenCodeSkill
        startupRegistryKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run'
        startupRegistryValue = 'AgentCallback'
        uninstallRegistryKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentCallback'
        uninstallCommand = $uninstallCommand
        installedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        installedProviders = @('codex', 'opencode')
        openCodePluginDirectory = $resolvedOpenCodePluginDirectory
        openCodePluginPath = $installedOpenCodePlugin
        providerNotes = [ordered]@{
            codex = 'Experimental Windows Desktop adapter; availability is checked through the current IPC endpoint rather than a package-version gate.'
            opencode = 'Experimental public plugin and loopback HTTP adapter; restart OpenCode after installation so the plugin can register its current server.'
        }
        dataRemovalPolicy = 'Preserved by default; use uninstall.ps1 -RemoveData only with explicit approval.'
    }
    Write-Utf8Json -Value $state -Path $installedState
    Write-Utf8Json -Value $state -Path $skillInstallation
    Write-Utf8Json -Value $state -Path $openCodeSkillInstallation
    if ($dataOwned) {
        Write-Utf8Json -Value ([ordered]@{ schemaVersion = 1; installId = $installId }) -Path $ownerPath
    }

    $startupKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $uninstallKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentCallback'
    $hostCommand = '"' + $installedExecutable + '" host run'
    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($startupKeyPath, $true)
    if ($null -eq $startupKey) { throw 'Could not create the current-user startup registry key.' }
    try {
        $startupKey.SetValue('AgentCallback', $hostCommand, [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $startupKey.Dispose()
    }

    $uninstallKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($uninstallKeyPath, $true)
    if ($null -eq $uninstallKey) { throw 'Could not create the current-user uninstall registry key.' }
    try {
        $estimatedSize = [Math]::Max(1, [Math]::Ceiling((Get-Item -LiteralPath $installedExecutable).Length / 1KB))
        $uninstallKey.SetValue('DisplayName', 'Agent Callback', [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('DisplayVersion', $Version, [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('Publisher', 'BIOcanse', [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('InstallLocation', $resolvedInstall, [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('DisplayIcon', $installedExecutable, [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('UninstallString', $uninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('QuietUninstallString', $uninstallCommand + ' -Quiet', [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('URLInfoAbout', 'https://github.com/BIOcanse/agent-callback', [Microsoft.Win32.RegistryValueKind]::String)
        $uninstallKey.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $uninstallKey.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $uninstallKey.SetValue('EstimatedSize', [int]$estimatedSize, [Microsoft.Win32.RegistryValueKind]::DWord)
        $uninstallKey.SetValue('InstallDate', (Get-Date).ToString('yyyyMMdd'), [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $uninstallKey.Dispose()
    }

    $hostRunning = $false
    if (-not $DoNotStartHost) {
        $hostResult = & $installedExecutable host start | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $hostResult.running) {
            throw 'Agent Callback installed, but its Host did not start.'
        }

        $hostRunning = $true
    }

    [pscustomobject]@{
        installed = $true
        version = $Version
        executablePath = $installedExecutable
        skillDirectory = $resolvedSkill
        openCodeSkillDirectory = $resolvedOpenCodeSkill
        openCodePluginPath = $installedOpenCodePlugin
        dataDirectory = $dataDirectory
        dataOwned = $dataOwned
        hostRunning = $hostRunning
        uninstallCommand = $uninstallCommand
    } | ConvertTo-Json -Depth 4
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
