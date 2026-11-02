@echo off
setlocal
chcp 65001 >nul
if "%~1"=="--temporary" goto run
rem 설치 폴더가 삭제되어도 끝까지 실행되도록 두 스크립트를 임시 폴더로 옮겨 실행한다.
set "runner=%TEMP%\DeskCal-uninstall-%RANDOM%-%RANDOM%-%RANDOM%"
mkdir "%runner%" || goto failed
copy /y "%~dp0uninstall.ps1" "%runner%\uninstall.ps1" >nul || goto failed
copy /y "%~f0" "%runner%\uninstall.cmd" >nul || goto failed
cd /d "%TEMP%"
"%runner%\uninstall.cmd" --temporary
exit /b
:run
set "answer="
set /p "answer=데이터(일정·할 일 DB, Google 토큰)도 삭제할까요? (y/N) "
if /i "%answer%"=="y" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" -RemoveData
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
)
pause
exit /b
:failed
echo 제거 스크립트를 준비하지 못했습니다. 파일과 임시 폴더 권한을 확인하세요.
pause
exit /b 1
