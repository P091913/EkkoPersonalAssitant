# Builds bin\ekko.ehpk, Ekko as an installable Even Hub app (DR-44). This repo is public, so the tailnet address is
# filled in here and never committed.
$ErrorActionPreference = 'Stop'

$bin = Join-Path $PSScriptRoot 'bin'
$stage = Join-Path $bin 'ehpk'
$manifest = Join-Path $bin 'app.json'
$package = Join-Path $bin 'ekko.ehpk'

$server = 'https://' + (tailscale status --json | ConvertFrom-Json).Self.DNSName.TrimEnd('.') + ':8444'

# ReadAllText/WriteAllText: Windows PowerShell's Get-Content and Set-Content would mangle the UTF-8.
function Stamp($from, $to, $old, $new) {
    $text = [IO.File]::ReadAllText($from)
    if (-not $text.Contains($old)) { throw "$from no longer contains: $old" }
    [IO.File]::WriteAllText($to, $text.Replace($old, $new))
}

Remove-Item $stage, $manifest, $package -Recurse -Force -ErrorAction Ignore
New-Item -ItemType Directory $stage | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'wwwroot\*') $stage -Recurse

Stamp (Join-Path $PSScriptRoot 'app.json') $manifest 'https://TAILNET-ADDRESS:8444' $server
Stamp (Join-Path $stage 'app.js') (Join-Path $stage 'app.js') "const SERVER = '';" "const SERVER = '$server';"

npx -y @evenrealities/evenhub-cli@0.2.0 pack $manifest $stage --sdk-ver 0.0.16 -o $package
if ($LASTEXITCODE -ne 0) { throw 'evenhub pack failed.' }
Write-Host "Built $package for $server"
