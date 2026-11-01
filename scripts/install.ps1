# DeskCal 설치(현재 사용자). 관리자 권한이 필요 없다.
# 1) %LOCALAPPDATA%\Programs\DeskCal에 복사  2) 시작 메뉴 바로 가기  3) Claude에 MCP 서버 등록  4) 앱 실행
# 데이터(DB, 토큰, client_secret.json)는 %LOCALAPPDATA%\DeskCal에 있어 다시 설치해도 지워지지 않는다.
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\DeskCal'
$app = Join-Path $target 'DeskCal.exe'
$mcp = Join-Path $target 'DeskCal.Mcp.exe'

# 실행 중이면 끈다(파일이 잠겨 복사가 안 된다). Claude가 MCP 서버를 쓰는 중이면 Claude를 먼저 닫아야 한다.
Get-Process -Name 'DeskCal' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source 'DeskCal.exe') $app -Force
Copy-Item (Join-Path $source 'DeskCal.Mcp.exe') $mcp -Force
Write-Output "복사했습니다: $target"

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskCal.lnk'))
$link.TargetPath = $app
$link.WorkingDirectory = $target
$link.Save()
Write-Output '시작 메뉴에 DeskCal을 만들었습니다.'

# Claude Code: claude 명령이 있으면 사용자 범위로 등록한다(이미 있으면 바꾼다).
if (Get-Command claude -ErrorAction SilentlyContinue) {
    & claude mcp remove deskcal --scope user 2>$null | Out-Null
    & claude mcp add deskcal --scope user -- $mcp
    Write-Output 'Claude Code에 MCP 서버(deskcal)를 등록했습니다.'
}

# Claude Desktop: 설정 파일이 있으면 mcpServers.deskcal만 넣거나 바꾼다. 원래 파일은 .bak으로 남긴다.
$desktopConfig = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
if (Test-Path $desktopConfig) {
    Copy-Item $desktopConfig "$desktopConfig.bak" -Force
    $config = Get-Content $desktopConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $config.PSObject.Properties['mcpServers']) {
        $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
    }
    $entry = [pscustomobject]@{ command = $mcp; args = @() }
    if ($config.mcpServers.PSObject.Properties['deskcal']) { $config.mcpServers.deskcal = $entry }
    else { $config.mcpServers | Add-Member -NotePropertyName deskcal -NotePropertyValue $entry }
    [IO.File]::WriteAllText($desktopConfig, ($config | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
    Write-Output 'Claude Desktop에 MCP 서버(deskcal)를 등록했습니다. Claude Desktop을 다시 시작하세요.'
}

Start-Process $app
Write-Output ''
Write-Output 'DeskCal을 실행했습니다. 알림 영역(트레이) 아이콘을 오른쪽 클릭 → "Google 연결"로 같은 Google 계정에 연결하세요.'
Write-Output '처음이면 OAuth 클라이언트 파일(client_secret….json)을 고르는 창이 뜹니다. 다른 PC의 %LOCALAPPDATA%\DeskCal\client_secret.json을 복사해 오면 됩니다.'
