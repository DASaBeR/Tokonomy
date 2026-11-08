# Taskrail installer for Windows.
#   irm https://github.com/taskrail/taskrail/releases/latest/download/install.ps1 | iex
# Environment:
#   TASKRAIL_VERSION      version to install (default: latest), e.g. 0.1.0
#   TASKRAIL_INSTALL_DIR  target directory (default: %LOCALAPPDATA%\Programs\taskrail)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = if ($env:TASKRAIL_REPO) { $env:TASKRAIL_REPO } else { 'taskrail/taskrail' }
$installDir = if ($env:TASKRAIL_INSTALL_DIR) { $env:TASKRAIL_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\taskrail' }

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
    'X64'   { $rid = 'win-x64' }
    'Arm64' { $rid = 'win-arm64' }
    default { throw "Unsupported architecture: $arch" }
}

$version = $env:TASKRAIL_VERSION
if (-not $version) {
    $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'taskrail-installer' }
    $version = $latest.tag_name
}
$version = $version.TrimStart('v')

$archive = "taskrail-$version-$rid.zip"
$base = "https://github.com/$repo/releases/download/v$version"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("taskrail-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    Write-Host "Downloading taskrail $version ($rid)..."
    Invoke-WebRequest -Uri "$base/$archive" -OutFile (Join-Path $tmp $archive) -UseBasicParsing
    Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS') -UseBasicParsing

    $line = Get-Content (Join-Path $tmp 'SHA256SUMS') | Where-Object { $_ -match "\s\*?$([regex]::Escape($archive))$" } | Select-Object -First 1
    if (-not $line) { throw "No checksum for $archive" }
    $expected = ($line -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $archive)).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "Checksum mismatch for $archive (expected $expected, got $actual)" }

    Expand-Archive -Path (Join-Path $tmp $archive) -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item (Join-Path $tmp 'taskrail.exe') (Join-Path $installDir 'taskrail.exe') -Force
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not $userPath) { $userPath = '' }
if (-not (($userPath -split ';') -contains $installDir)) {
    [Environment]::SetEnvironmentVariable('Path', (($userPath.TrimEnd(';'), $installDir) -join ';'), 'User')
    Write-Host "Added $installDir to your user PATH. Open a new terminal to use it."
}

Write-Host "Installed taskrail $version to $installDir\taskrail.exe"
