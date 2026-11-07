# Publishes a built release (release\*) to GitHub Releases - this is what the auto-updater reads.
#   powershell -ExecutionPolicy Bypass -File publish_release.ps1 -Version 1.1.0 [-Notes "release notes"]
# Needs GitHub CLI logged in once:  gh auth login
param([Parameter(Mandatory = $true)][string]$Version, [string]$Notes = '')
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoFile = Join-Path $here 'repo.txt'
if (-not (Test-Path $repoFile)) { throw 'repo.txt not found (owner/KeidoScreen)' }
$repo = (Get-Content $repoFile -Raw).Trim()

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\msys64\ucrt64\bin\gh.exe' }
if (-not (Test-Path $gh)) { throw 'GitHub CLI (gh) not found: pacman -S mingw-w64-ucrt-x86_64-github-cli' }

$out = Join-Path $here 'release'
$files = @("KeidoScreen-Setup-$Version.exe", "KeidoScreen-$Version-portable.zip", 'SHA256SUMS.txt') | ForEach-Object { Join-Path $out $_ }
foreach ($f in $files) { if (-not (Test-Path $f)) { throw "missing $f - run build_release.ps1 -Version $Version first" } }

# the checksums must belong to exactly these files, otherwise the updater refuses to install
$sums = Get-Content (Join-Path $out 'SHA256SUMS.txt')
foreach ($f in $files[0..1]) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    if (-not ($sums -match "^$h\s+\*?$([regex]::Escape((Split-Path $f -Leaf)))$")) { throw "SHA256SUMS.txt does not match $f" }
}

if (-not $Notes) { $Notes = "KeidoScreen $Version" }
& $gh release create "v$Version" @files --repo $repo --title "KeidoScreen $Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "Published: https://github.com/$repo/releases/tag/v$Version"
