# DeskCal 설치(현재 사용자). 관리자 권한이 필요 없다.
# 1) %LOCALAPPDATA%\Programs\DeskCal에 복사  2) 시작 메뉴 바로 가기  3) Claude에 MCP 서버 등록  4) 앱 실행
# 데이터(DB, 토큰, client_secret.json)는 %LOCALAPPDATA%\DeskCal에 있어 다시 설치해도 지워지지 않는다.
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\DeskCal'
$app = Join-Path $target 'DeskCal.exe'
$mcp = Join-Path $target 'DeskCal.Mcp.exe'

# 실행 중이면 끈다(파일이 잠겨 복사가 안 된다). Claude가 띄운 MCP 서버도 끄므로 설치 뒤 Claude를 다시 시작해야 한다.
Get-Process -Name 'DeskCal', 'DeskCal.Mcp' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $source 'DeskCal.exe') $app -Force
Copy-Item (Join-Path $source 'DeskCal.Mcp.exe') $mcp -Force
Copy-Item (Join-Path $source 'uninstall.ps1') $target -Force
Copy-Item (Join-Path $source 'uninstall.cmd') $target -Force
Write-Output "복사했습니다: $target"

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskCal.lnk'))
$link.TargetPath = $app
$link.WorkingDirectory = $target
$link.Save()
Write-Output '시작 메뉴에 DeskCal을 만들었습니다.'

# Claude Code: claude 명령이 있으면 사용자 범위로 등록한다(이미 있으면 바꾼다).
# Windows PowerShell 5.1은 네이티브 명령의 stderr를 오류로 바꾸므로 이 구간만 'Continue'로 둔다(등록이 없으면 remove가 실패한다).
if (Get-Command claude -ErrorAction SilentlyContinue) {
    $ErrorActionPreference = 'Continue'
    & claude mcp remove deskcal --scope user 2>$null | Out-Null
    & claude mcp add deskcal --scope user -- $mcp
    $added = $LASTEXITCODE -eq 0
    $ErrorActionPreference = 'Stop'
    if ($added) { Write-Output 'Claude Code에 MCP 서버(deskcal)를 등록했습니다.' }
    else { Write-Output "Claude Code 등록에 실패했습니다. 직접 실행하세요: claude mcp add deskcal --scope user -- `"$mcp`"" }
}

# Claude Desktop: 설정 파일이 있으면 mcpServers.deskcal만 넣거나 바꾼다. 원래 파일은 .bak으로 남긴다.
# 일반 설치는 %APPDATA%\Claude, Microsoft Store(MSIX) 설치는 %LOCALAPPDATA%\Claude-Data\claude_<게시자ID>에 둔다.
$desktopConfigs = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json') +
    @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Claude-Data') -Directory -Filter 'claude_*' -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'claude_desktop_config.json' })
$registered = $false
foreach ($desktopConfig in $desktopConfigs) {
    if (-not (Test-Path $desktopConfig)) { continue }
    $registered = $true
    try {
        # Windows PowerShell 5.1에서도 stderr 때문에 다른 설정 등록과 앱 실행이 중단되지 않게 한다.
        $ErrorActionPreference = 'Continue'
        & $mcp --register-claude-desktop $desktopConfig
        if ($LASTEXITCODE -eq 0) {
            Write-Output "Claude Desktop에 MCP 서버(deskcal)를 등록했습니다: $desktopConfig"
        }
        else { Write-Warning "Claude Desktop 등록에 실패했습니다: $desktopConfig" }
    }
    catch { Write-Warning "Claude Desktop 등록에 실패했습니다: $desktopConfig ($_)" }
    finally { $ErrorActionPreference = 'Stop' }
}
if ($registered) { Write-Output 'Claude Desktop을 완전히 종료(알림 영역 → 종료)한 뒤 다시 시작하세요.' }
else { Write-Output "Claude Desktop 설정 파일을 찾지 못했습니다. Claude Desktop → 설정 → 개발자 → 설정 편집에서 mcpServers에 `"deskcal`": {`"command`": `"$($mcp -replace '\\','\\')`"}를 넣으세요." }

Start-Process $app
Write-Output ''
Write-Output 'DeskCal을 실행했습니다. 알림 영역(트레이) 아이콘을 오른쪽 클릭 → "Google 연결"로 같은 Google 계정에 연결하세요.'
Write-Output '처음이면 OAuth 클라이언트 파일(client_secret….json)을 고르는 창이 뜹니다. 다른 PC의 %LOCALAPPDATA%\DeskCal\client_secret.json을 복사해 오면 됩니다.'
