# Tokonomy installer for Windows.
#   irm https://github.com/DASaBeR/Tokonomy/releases/latest/download/install.ps1 | iex
# Environment:
#   TOKONOMY_VERSION      version to install (default: latest), e.g. 0.1.0
#   TOKONOMY_INSTALL_DIR  target directory (default: %LOCALAPPDATA%\Programs\tokonomy)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo = if ($env:TOKONOMY_REPO) { $env:TOKONOMY_REPO } else { 'tokonomy/tokonomy' }
$installDir = if ($env:TOKONOMY_INSTALL_DIR) { $env:TOKONOMY_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\tokonomy' }

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
switch ($arch) {
    'X64'   { $rid = 'win-x64' }
    'Arm64' { $rid = 'win-arm64' }
    default { throw "Unsupported architecture: $arch" }
}

$version = $env:TOKONOMY_VERSION
if (-not $version) {
    $latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'tokonomy-installer' }
    $version = $latest.tag_name
}
$version = $version.TrimStart('v')

$archive = "tokonomy-$version-$rid.zip"
$base = "https://github.com/$repo/releases/download/v$version"
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("tokonomy-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    Write-Host "Downloading tokonomy $version ($rid)..."
    Invoke-WebRequest -Uri "$base/$archive" -OutFile (Join-Path $tmp $archive) -UseBasicParsing
    Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $tmp 'SHA256SUMS') -UseBasicParsing

    $line = Get-Content (Join-Path $tmp 'SHA256SUMS') | Where-Object { $_ -match "\s\*?$([regex]::Escape($archive))$" } | Select-Object -First 1
    if (-not $line) { throw "No checksum for $archive" }
    $expected = ($line -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tmp $archive)).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "Checksum mismatch for $archive (expected $expected, got $actual)" }

    Expand-Archive -Path (Join-Path $tmp $archive) -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    Copy-Item (Join-Path $tmp 'tokonomy.exe') (Join-Path $installDir 'tokonomy.exe') -Force
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

Write-Host "Installed tokonomy $version to $installDir\tokonomy.exe"
