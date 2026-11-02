param([switch]$RemoveData)

# 현재 사용자 설치만 제거한다. 데이터는 명시적으로 요청한 경우에만 지운다.
$ErrorActionPreference = 'Stop'
$localRoot = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
$target = Join-Path $localRoot 'Programs\DeskCal'
$data = Join-Path $localRoot 'DeskCal'
$mcp = Join-Path $target 'DeskCal.Mcp.exe'

function Invoke-RemovalStep($name, [scriptblock]$action) {
    try { & $action }
    catch { Write-Warning "$name 실패: $_" }
}

# 프로세스 종료
Invoke-RemovalStep '프로세스 종료' {
    Get-Process -Name 'DeskCal', 'DeskCal.Mcp' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# Claude Desktop 등록 해제: 실행 파일을 지우기 전에 처리한다.
Invoke-RemovalStep 'Claude Desktop 설정 검색' {
    $desktopConfigs = @(Join-Path $env:APPDATA 'Claude\claude_desktop_config.json') +
        @(Get-ChildItem (Join-Path $localRoot 'Claude-Data') -Directory -Filter 'claude_*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'claude_desktop_config.json' })
    foreach ($desktopConfig in $desktopConfigs) {
        Invoke-RemovalStep "Claude Desktop 등록 해제 ($desktopConfig)" {
            if (-not (Test-Path -LiteralPath $desktopConfig)) { return }
            if (-not (Test-Path -LiteralPath $mcp)) { throw 'MCP 실행 파일이 없어 등록을 해제하지 못했습니다.' }
            try {
                # Windows PowerShell 5.1의 네이티브 stderr도 다음 파일 처리를 막지 않는다.
                $ErrorActionPreference = 'Continue'
                & $mcp --unregister-claude-desktop $desktopConfig
                if ($LASTEXITCODE -ne 0) { throw "종료 코드: $LASTEXITCODE" }
                Write-Output "Claude Desktop 등록을 해제했습니다: $desktopConfig"
            }
            finally { $ErrorActionPreference = 'Stop' }
        }
    }
}

# Claude Code 등록 해제
Invoke-RemovalStep 'Claude Code 등록 해제' {
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        try {
            $ErrorActionPreference = 'Continue'
            & claude mcp remove deskcal --scope user
            if ($LASTEXITCODE -ne 0) { throw "종료 코드: $LASTEXITCODE" }
            Write-Output 'Claude Code 등록을 해제했습니다.'
        }
        finally { $ErrorActionPreference = 'Stop' }
    }
}

# 시작 시 실행 등록 해제
Invoke-RemovalStep '시작 시 실행 등록 해제' {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runValues = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
    if ($null -ne $runValues -and $runValues.PSObject.Properties['DeskCal']) {
        Remove-ItemProperty -LiteralPath $runKey -Name 'DeskCal'
        Write-Output '시작 시 실행 등록을 삭제했습니다.'
    }
}

# 시작 메뉴 바로 가기 삭제
Invoke-RemovalStep '시작 메뉴 바로 가기 삭제' {
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskCal.lnk'
    if (Test-Path -LiteralPath $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
        Write-Output '시작 메뉴의 DeskCal 바로 가기를 삭제했습니다.'
    }
}

# 프로그램과 선택한 데이터만 삭제한다. 삭제 경로가 현재 사용자 폴더 아래인지 먼저 확인한다.
Invoke-RemovalStep '프로그램 폴더 삭제' {
    $resolvedTarget = [IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($localRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '잘못된 프로그램 경로입니다.' }
    if (Test-Path -LiteralPath $resolvedTarget) { Remove-Item -LiteralPath $resolvedTarget -Recurse -Force }
    Write-Output "프로그램 폴더를 제거했습니다: $resolvedTarget"
}
if ($RemoveData) {
    Invoke-RemovalStep '데이터 삭제' {
        $resolvedData = [IO.Path]::GetFullPath($data)
        if (-not $resolvedData.StartsWith($localRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '잘못된 데이터 경로입니다.' }
        if (Test-Path -LiteralPath $resolvedData) { Remove-Item -LiteralPath $resolvedData -Recurse -Force }
        Write-Output "데이터 폴더를 제거했습니다: $resolvedData"
    }
}
if (Test-Path -LiteralPath $data) { Write-Output "남겨 둔 데이터: $data" }
else { Write-Output "남아 있는 데이터 폴더가 없습니다: $data" }
if (Test-Path -LiteralPath $target) { Write-Warning "프로그램 파일이 남아 있습니다: $target" }
Write-Output '제거 작업이 끝났습니다. 위 경고가 있으면 해당 항목을 확인하세요.'
Write-Output 'Claude Desktop을 완전히 종료(알림 영역 → 종료)한 뒤 다시 시작하세요.'
