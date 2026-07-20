[CmdletBinding()]
param(
    [switch]$RemoveData,
    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Remove-BoundedTree {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AllowedRoot,

        [int]$MaximumEntries = 1000
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $rootPrefix = [System.IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside its allowed root: $resolvedPath"
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
                throw "Uninstall cleanup exceeded the $MaximumEntries entry safety limit."
            }

            $entryPath = [System.IO.Path]::GetFullPath($entry.FullName)
            if (-not $entryPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Uninstall cleanup escaped its allowed root: $entryPath"
            }

            if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Uninstall refuses to traverse a reparse point: $entryPath"
            }

            if ($entry.PSIsContainer) { $pending.Push($entryPath) } else { $files.Add($entryPath) }
        }
    }

    foreach ($file in $files) { Remove-Item -LiteralPath $file -Force }
    foreach ($directory in ($directories | Sort-Object { $_.Length } -Descending)) {
        Remove-Item -LiteralPath $directory -Force
    }
}

try {
    $installDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
    $programsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
    $programsPrefix = $programsRoot.TrimEnd('\') + '\'
    if (-not $installDirectory.StartsWith($programsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Uninstaller is not running from the expected per-user Programs root: $installDirectory"
    }

    $statePath = Join-Path $installDirectory 'install-state.json'
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "Install state is missing: $statePath"
    }

    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($state.installId) -or
        [System.IO.Path]::GetFullPath($state.installDirectory) -ne $installDirectory) {
        throw 'Install state does not own this installation directory.'
    }

    $executable = [System.IO.Path]::GetFullPath($state.executablePath)
    if (Test-Path -LiteralPath $executable -PathType Leaf) {
        & $executable host stop 2>$null | Out-Null
        Start-Sleep -Milliseconds 400
    }

    $startupKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
        'Software\Microsoft\Windows\CurrentVersion\Run',
        $true)
    if ($null -ne $startupKey) {
        try { $startupKey.DeleteValue('AgentCallback', $false) }
        finally { $startupKey.Dispose() }
    }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(
        'Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentCallback',
        $false)

    $skillDirectory = [System.IO.Path]::GetFullPath($state.skillDirectory)
    $skillsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.codex\skills'))
    $skillOwnerPath = Join-Path $skillDirectory 'references\app-installation.json'
    if (Test-Path -LiteralPath $skillOwnerPath -PathType Leaf) {
        $skillOwner = Get-Content -LiteralPath $skillOwnerPath -Raw | ConvertFrom-Json
        if ($skillOwner.installId -eq $state.installId) {
            Remove-BoundedTree -Path $skillDirectory -AllowedRoot $skillsRoot
        }
        else {
            throw 'Installed Skill ownership does not match the App installation.'
        }
    }

    $dataRemoved = $false
    if ($RemoveData) {
        if ($state.dataOwned -ne $true) {
            throw 'This installation does not own the existing data directory; it was not removed.'
        }

        $dataDirectory = [System.IO.Path]::GetFullPath($state.dataDirectory)
        $expectedData = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AgentCallback'))
        $ownerPath = Join-Path $dataDirectory 'install-owner.json'
        if ($dataDirectory -ne $expectedData -or -not (Test-Path -LiteralPath $ownerPath -PathType Leaf)) {
            throw 'Data directory ownership could not be verified; it was not removed.'
        }

        $dataOwner = Get-Content -LiteralPath $ownerPath -Raw | ConvertFrom-Json
        if ($dataOwner.installId -ne $state.installId) {
            throw 'Data directory belongs to a different installation; it was not removed.'
        }

        Remove-BoundedTree -Path $dataDirectory -AllowedRoot $env:LOCALAPPDATA -MaximumEntries 10000
        $dataRemoved = $true
    }

    $result = [pscustomobject]@{
        uninstalled = $true
        installDirectory = $installDirectory
        skillDirectory = $skillDirectory
        dataDirectory = $state.dataDirectory
        dataRemoved = $dataRemoved
    }
    Remove-BoundedTree -Path $installDirectory -AllowedRoot $programsRoot
    if (-not $Quiet) { $result | ConvertTo-Json -Depth 3 }
    exit 0
}
catch {
    if (-not $Quiet) { [Console]::Error.WriteLine($_.Exception.Message) }
    exit 1
}
