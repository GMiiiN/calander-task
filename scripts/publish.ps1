# 배포 묶음을 만든다: artifacts\DeskCal-win-x64.zip
# 앱(DeskCal.exe)과 MCP 서버(DeskCal.Mcp.exe)를 .NET 런타임이 없는 PC에서도 돌도록 단일 exe로 만들고 설치 스크립트를 함께 넣는다.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\DeskCal'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', $out)
dotnet publish (Join-Path $root 'src\DeskCal.App\DeskCal.App.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'DeskCal.App publish failed' }
dotnet publish (Join-Path $root 'src\DeskCal.Mcp\DeskCal.Mcp.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'DeskCal.Mcp publish failed' }

Copy-Item (Join-Path $PSScriptRoot 'install.ps1') $out
Copy-Item (Join-Path $PSScriptRoot 'install.cmd') $out
$zip = Join-Path $root 'artifacts\DeskCal-win-x64.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Output "만들었습니다: $zip"
