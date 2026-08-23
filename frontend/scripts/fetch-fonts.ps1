# Downloads Vazirmatn (OFL-licensed Persian font) for self-hosting.
# Run once; the woff2 files are committed so builds/deploys need no network.
$ErrorActionPreference = "Stop"
$base = "https://cdn.jsdelivr.net/gh/rastikerdar/vazirmatn@v33.003"
$out = Join-Path $PSScriptRoot "..\public\fonts"
New-Item -ItemType Directory -Force -Path $out | Out-Null

$files = @(
  @{ url = "$base/Vazirmatn-font-face.css";                name = "Vazirmatn-font-face.css" },
  @{ url = "$base/fonts/webfonts/Vazirmatn-Regular.woff2"; name = "Vazirmatn-Regular.woff2" },
  @{ url = "$base/fonts/webfonts/Vazirmatn-Medium.woff2";  name = "Vazirmatn-Medium.woff2"  },
  @{ url = "$base/fonts/webfonts/Vazirmatn-Bold.woff2";    name = "Vazirmatn-Bold.woff2"    }
)

foreach ($f in $files) {
  $dest = Join-Path $out $f.name
  Write-Host "Downloading $($f.name)..."
  Invoke-WebRequest -Uri $f.url -OutFile $dest -UseBasicParsing
}

Get-ChildItem $out | Format-Table Name, Length
Write-Host "Done. Fonts are self-hosted under public/fonts."