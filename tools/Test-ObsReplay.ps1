$ErrorActionPreference = "Stop"

$root = Join-Path $env:RUNNER_TEMP "clipbridge-obs"
$installation = Join-Path $root "obs"
$replays = Join-Path $root "replays"
$zip = Join-Path $root "obs.zip"
$bin = Join-Path $installation "bin/64bit"
$config = Join-Path $installation "config/obs-studio"
$profile = Join-Path $config "basic/profiles/ClipBridge"
$scenes = Join-Path $config "basic/scenes"
$websocket = Join-Path $config "plugin_config/obs-websocket"
$url = "https://github.com/obsproject/obs-studio/releases/download/33.0.1/OBS-Studio-33.0.1-Windows-x64.zip"

New-Item -ItemType Directory -Force $installation, $replays | Out-Null
curl.exe --fail --location --retry 2 --max-time 120 --output $zip $url
if ($LASTEXITCODE -ne 0) { throw "OBS Studio could not be downloaded." }
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne "0d9436451c2d772b33428e3fdae9776f071895c335de17283176306fb72651d3") {
  throw "OBS Studio archive SHA-256 does not match."
}
tar.exe -xf $zip -C $installation
if ($LASTEXITCODE -ne 0) { throw "OBS Studio could not be extracted." }
New-Item -ItemType Directory -Force $profile, $scenes, $websocket | Out-Null

@'
[General]
FirstRun=true

[Basic]
Profile=ClipBridge
ProfileDir=ClipBridge
SceneCollection=ClipBridge
SceneCollectionFile=ClipBridge
'@ | Set-Content -LiteralPath (Join-Path $config "user.ini") -Encoding utf8NoBOM

@'
[General]
Name=ClipBridge

[Output]
Mode=Simple

[Video]
BaseCX=640
BaseCY=360
OutputCX=640
OutputCY=360
FPSType=0
FPSCommon=30

[SimpleOutput]
RecQuality=Small
RecEncoder=x264
RecFormat2=hybrid_mp4
RecRB=true
'@ | Set-Content -LiteralPath (Join-Path $profile "basic.ini") -Encoding utf8NoBOM

@'
{
  "name": "ClipBridge",
  "current_scene": "Synthetic",
  "current_program_scene": "Synthetic",
  "scene_order": [{ "name": "Synthetic" }],
  "sources": [
    {
      "name": "Signal",
      "id": "color_source",
      "versioned_id": "color_source_v3",
      "enabled": true,
      "settings": { "color": 4278255360, "width": 640, "height": 360 }
    },
    {
      "name": "Synthetic",
      "id": "scene",
      "versioned_id": "scene",
      "enabled": true,
      "settings": { "id_counter": 1, "items": [
        { "name": "Signal", "id": 1, "visible": true, "scale": { "x": 1.0, "y": 1.0 } }
      ]}
    }
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $scenes "ClipBridge.json") -Encoding utf8NoBOM

$env:CLIPBRIDGE_OBS_SMOKE_PASSWORD = [Guid]::NewGuid().ToString("N")
@{
  first_load = $false
  server_enabled = $true
  server_port = 4455
  alerts_enabled = $false
  auth_required = $true
  server_password = $env:CLIPBRIDGE_OBS_SMOKE_PASSWORD
} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $websocket "config.json") -Encoding utf8NoBOM

$obsArguments = @("--portable", "--profile", "ClipBridge", "--collection", "ClipBridge", "--disable-shutdown-check", "--disable-updater")
$obs = Start-Process -FilePath (Join-Path $bin "obs64.exe") -WorkingDirectory $bin -PassThru -ArgumentList $obsArguments
try {
  $ready = $false
  $deadline = [DateTime]::UtcNow.AddSeconds(45)
  while ([DateTime]::UtcNow -lt $deadline -and -not $obs.HasExited) {
    $tcp = [System.Net.Sockets.TcpClient]::new()
    try {
      $tcp.Connect("127.0.0.1", 4455)
      $ready = $true
      break
    } catch [System.Net.Sockets.SocketException] {
      Start-Sleep -Milliseconds 750
    } finally {
      $tcp.Dispose()
    }
  }
  if (-not $ready) { throw "OBS Studio did not open its local WebSocket port." }
  dotnet run --project tools/ClipBridge.Bench -c Release -- obs-smoke $replays
  if ($LASTEXITCODE -ne 0) { throw "ClipBridge did not save two OBS Studio replay files." }
} finally {
  if (-not $obs.HasExited) {
    Stop-Process -Id $obs.Id -Force
  }
  $latestLog = Get-ChildItem (Join-Path $config "logs") -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($latestLog) { Get-Content -LiteralPath $latestLog.FullName -Tail 100 }
  Remove-Item Env:CLIPBRIDGE_OBS_SMOKE_PASSWORD -ErrorAction SilentlyContinue
}
