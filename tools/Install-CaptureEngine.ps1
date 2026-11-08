param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = "Stop"

$release = "autobuild-2026-11-08-12-35"
$asset = "ffmpeg-n8.1.3-34-ga35157abb9-win64-lgpl-8.1.zip"
$hash = "d1172b6bf0ffd75cf93c3362585c0c0fb62c3caef6d93df5add6ac64779f8f73"
$url = "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/$release/$asset"
$staging = Join-Path ([IO.Path]::GetTempPath()) ("clipbridge-ffmpeg-" + [Guid]::NewGuid().ToString("N"))
try {
  New-Item -ItemType Directory -Path $staging | Out-Null
  $zip = Join-Path $staging "engine.zip"
  curl.exe --fail --location --retry 2 --max-time 120 --output $zip $url
  if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $hash) {
    throw "FFmpeg download or SHA-256 verification failed."
  }
  tar.exe -xf $zip -C $staging
  if ($LASTEXITCODE -ne 0) { throw "FFmpeg extraction failed." }
  $engine = Get-ChildItem -LiteralPath $staging -Directory | Select-Object -First 1
  $ffmpeg = Join-Path $engine.FullName "bin/ffmpeg.exe"
  $filters = & $ffmpeg -hide_banner -filters
  if ($LASTEXITCODE -ne 0 -or -not ($filters -match "\bgfxcapture\b")) {
    throw "The pinned FFmpeg build is missing Windows Graphics Capture."
  }
  New-Item -ItemType Directory -Force -Path $Destination, (Join-Path $Destination "bin") | Out-Null
  Copy-Item -LiteralPath $ffmpeg -Destination (Join-Path $Destination "bin/ffmpeg.exe")
  Get-ChildItem -LiteralPath $engine.FullName -File | Copy-Item -Destination $Destination
  @"
FFmpeg binary and build release: $url
FFmpeg corresponding source: https://github.com/FFmpeg/FFmpeg/tree/a35157abb9
Build scripts and patches: https://github.com/FFmpeg-Builds/FFmpeg-Builds/tree/$release
"@ | Set-Content -LiteralPath (Join-Path $Destination "SOURCE.txt") -Encoding utf8
} finally {
  if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
