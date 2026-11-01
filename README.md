# calander-task (DeskCal)

Windows 바탕화면에 붙어 있는 달력·할 일 앱입니다. Claude가 MCP로 일정과 할 일을 읽고 쓸 수 있고, Google 캘린더·Tasks와 동기화합니다. 지금은 공휴일만 받아 오고, 일정·할 일 동기화는 개발 중입니다. 설계는 [docs/design.md](docs/design.md)에 있습니다.

## 설치 (PC마다)

1. 배포 묶음 `DeskCal-win-x64.zip`을 받아 압축을 풉니다. 직접 만들려면 저장소에서 `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`을 실행하세요. `artifacts\`에 만들어집니다. .NET이 설치되어 있지 않은 PC에서도 실행됩니다.
2. 압축을 푼 폴더에서 `install.cmd`를 두 번 누릅니다. 설치 스크립트가 하는 일은 다음과 같습니다.
   - `%LOCALAPPDATA%\Programs\DeskCal`에 복사하고 시작 메뉴에 바로 가기를 만듭니다. 관리자 권한은 필요 없습니다.
   - `claude` 명령이 있으면 Claude Code에 MCP 서버 `deskcal`을 사용자 범위로 등록합니다.
   - Claude Desktop 설정 파일(`%APPDATA%\Claude\claude_desktop_config.json`)이 있으면 `mcpServers.deskcal`을 넣습니다. 원래 파일은 `.bak`으로 남깁니다.
   - 설치가 끝나면 앱을 실행합니다.
3. 알림 영역 아이콘을 오른쪽 클릭하고 **Google 연결**을 누릅니다.
   - 처음이면 OAuth 클라이언트 파일을 고르는 창이 뜹니다. 이미 쓰던 PC의 `%LOCALAPPDATA%\DeskCal\client_secret.json`을 복사해 와서 고르면 됩니다.
   - 브라우저에서 **처음 연결한 것과 같은 Google 계정**으로 로그인합니다.
4. 시작할 때 자동으로 실행하려면 알림 영역 메뉴에서 **시작 시 실행**을 켭니다.

다시 설치해도 데이터(`%LOCALAPPDATA%\DeskCal`의 DB와 토큰)는 그대로 남습니다.

## OAuth 클라이언트 파일

Google Cloud 콘솔에서 사용자 본인의 프로젝트를 만들고, Calendar·Tasks·Drive API를 켭니다. OAuth 클라이언트는 **데스크톱 앱** 유형으로 만들고 JSON을 받습니다. 이 파일은 저장소에 넣지 않습니다.

OAuth 동의 화면이 "테스트" 상태이면 7일마다 다시 로그인해야 합니다.

## 개발

```
dotnet build
dotnet test
```
