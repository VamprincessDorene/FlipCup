# Builds Flip Cup, updates repo.json, pushes, and publishes a GitHub release.
# Usage (from this folder):  .\publish.ps1
# Bump <Version> in FlipCup.csproj first; Dalamud only offers an update when the version goes up.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Which GitHub repo this is, from the git remote (e.g. https://github.com/someone/FlipCup.git).
$remote = git remote get-url origin
if ($remote -notmatch "github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)") { throw "origin isn't a GitHub repo: $remote" }
$owner = $Matches.owner; $repo = $Matches.repo
$repoUrl = "https://github.com/$owner/$repo"

dotnet build -c Release -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$zip = Join-Path $PSScriptRoot "bin\Release\FlipCup\latest.zip"
$manifest = Get-Content (Join-Path $PSScriptRoot "bin\Release\FlipCup\FlipCup.json") -Raw | ConvertFrom-Json
$version = $manifest.AssemblyVersion
$download = "$repoUrl/releases/download/v$version/latest.zip"

# repo.json: the list Dalamud reads from the custom repository URL.
$entry = [ordered]@{}
foreach ($p in $manifest.PSObject.Properties) { $entry[$p.Name] = $p.Value }
$entry["Author"] = $owner
$entry["RepoUrl"] = $repoUrl
$entry["DownloadLinkInstall"] = $download
$entry["DownloadLinkUpdate"] = $download
$entry["DownloadLinkTesting"] = $download
$entry["LastUpdate"] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$json = ConvertTo-Json @($entry) -Depth 5
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "repo.json"), $json, (New-Object Text.UTF8Encoding $false))

git add -A
git commit -m "Release v$version"
git push origin HEAD

gh release create "v$version" $zip --repo "$owner/$repo" --title "Flip Cup v$version" --notes "Flip Cup v$version"

Write-Host ""
Write-Host "Published v$version."
Write-Host "Custom repo URL for friends: https://raw.githubusercontent.com/$owner/$repo/main/repo.json"
