---
title: DeskCal 개인정보처리방침
---

# DeskCal 개인정보처리방침

시행일: 2026년 11월 25일

DeskCal(이하 "앱")은 사용자의 PC에서만 동작하는 데스크톱 앱입니다. 개발자는 별도의 서버를 운영하지 않으며, 사용자의 일정·할 일·계정 정보를 받거나 보관하지 않습니다.

## 1. 저장하는 정보와 위치

- 일정·할 일·설정은 사용자 PC의 `%LOCALAPPDATA%\DeskCal` 폴더에 있는 로컬 데이터베이스에 저장됩니다. Google 연동을 켜면 아래 항목을 사용자 본인의 Google 계정과 동기화합니다.
- **Google 로그인 토큰**: Google 연동을 켠 경우에만, 같은 데이터베이스에 Windows 사용자 계정으로 암호화(DPAPI)하여 저장합니다. 다른 Windows 사용자나 다른 PC에서는 풀 수 없습니다.
- **Google 계정 식별자와 이메일**: 처음 연결한 계정과 같은 계정으로만 다시 연결되도록 확인하는 데 쓰며, 같은 데이터베이스에 저장합니다.
- **동기화 로그**: 오류 원인을 확인하기 위한 기록이 `%LOCALAPPDATA%\DeskCal\logs`에 저장됩니다. 외부로 보내지 않습니다.

## 2. Google 사용자 데이터 사용 (Google 연동을 켠 경우에만)

Google 연동은 선택 기능입니다. 켜지 않으면 앱은 Google에 접속해 사용자 데이터를 읽거나 쓰지 않습니다.

| 요청 권한 | 용도 |
|---|---|
| `openid`, `email` | 연결한 Google 계정을 식별하고 알림 영역 메뉴에 이메일을 표시 |
| Google Calendar 일정 (`calendar.events`) | 앱의 일정과 사용자 Google 캘린더의 기본 캘린더 일정을 서로 동기화(읽기·쓰기), 공휴일 표시 |
| Google Tasks (`tasks`) | 앱의 할 일과 사용자 Google Tasks 기본 목록을 서로 동기화(읽기·쓰기) |
| Google Drive 앱 전용 폴더 (`drive.appdata`) | 같은 계정의 PC에서 카테고리 별칭, 날짜별 일정 순서, 학기를 맞추기 위해 앱 전용 숨김 폴더에 기기별 공유 파일을 저장합니다. |

- Drive 공유 파일에는 Google 계정 식별자(`sub`), DeskCal이 만든 기기 ID, 카테고리 색 ID와 별칭, 날짜별 일정 ID 목록(반복 회차는 시리즈 ID와 원래 회차 시각), 학생 모드 사용 여부·개강일·총 주차가 들어갑니다. 각 설정에는 변경 버전과 작성 기기 ID, 변경 시각이 붙습니다.
- Google에서 받은 데이터는 사용자 PC와 사용자 본인의 Google 계정 사이에서만 오갑니다.
- 개발자나 제3자에게 전송·판매·공유하지 않으며, 광고나 다른 목적에 사용하지 않습니다.
- 앱의 Google 사용자 데이터 사용은 [Google API 서비스 사용자 데이터 정책](https://developers.google.com/terms/api-services-user-data-policy)(제한적 사용 요구사항 포함)을 따릅니다.

## 3. Claude 연동

앱에 포함된 MCP 서버는 사용자가 쓰는 Claude 앱(Claude Desktop, Claude Code)이 사용자 PC 안에서 실행합니다. Claude가 일정·할 일을 읽거나 쓰면 그 내용이 Claude 대화에 포함되며, 이는 사용자가 이용하는 Claude 서비스의 정책을 따릅니다. 앱 자체는 Claude 쪽으로 따로 데이터를 보내지 않습니다.

## 4. 보관과 삭제

- 앱을 제거해도 PC의 데이터는 기본으로 남습니다. 제거 스크립트(`uninstall.cmd`)에서 데이터 삭제를 선택하거나 `%LOCALAPPDATA%\DeskCal` 폴더를 지우면 해당 PC의 데이터가 삭제됩니다.
- Drive 앱 전용 폴더의 공유 설정과 다른 PC에 받은 사본은 로컬 삭제 후에도 남습니다. 공유 설정을 지우려면 연결한 모든 PC에서 DeskCal 동기화를 종료한 뒤 Google Drive의 설정에서 앱 관리로 들어가 DeskCal의 숨겨진 앱 데이터를 삭제합니다. 기존 설정이 있는 PC가 다시 동기화하면 공유 파일이 다시 생성될 수 있습니다. [Google Drive 도움말](https://support.google.com/drive/answer/6374270?hl=ko)
- Google 계정의 앱 접근 권한은 [Google 계정 > 보안 > 서드파티 앱 및 서비스](https://myaccount.google.com/connections)에서 언제든 해제할 수 있습니다.
- Google 캘린더·Tasks로 동기화된 일정은 사용자의 Google 계정에 남으며 Google에서 직접 관리할 수 있습니다.

## 5. 변경과 문의

이 방침이 바뀌면 이 페이지를 고치고 시행일을 갱신합니다. 문의: hacu1204@gmail.com
