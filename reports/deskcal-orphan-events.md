# deskCal 일정 수정 후 Google·아이폰에 옛 일정이 남는 원인

**가장 먼저 고칠 원인은 시간 지정 일정과 종일 일정 사이를 전환하는 PATCH다.** deskCal은 기존 Google ID에 새 형식의 `start`와 `end`를 보내면서 이전 형식의 필드를 지우는 JSON `null`을 생략한다. Google의 하위 필드 병합 규칙을 적용하면 `date`와 `dateTime`이 함께 남아 `400 Invalid start time.`이 발생한다. 이때 deskCal에는 새 형식이 보이고 Google에는 옛 형식 하나가 계속 남는다. 이 상태에서 새 일정을 **별도 생성**하면 두 Google ID가 모두 활성으로 남는다. 원래 편집 요청이 자동으로 새 ID를 만드는 경로는 발견되지 않았다. [시간 직렬화, EventSync.cs:261](../src/DeskCal.Core/EventSync.cs#L261) · [실패 처리, EventSync.cs:324](../src/DeskCal.Core/EventSync.cs#L324) · [양방향 재현](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L56)

**확실도는 전환 결함에 대해 높고, 오너의 개별 일정에 대해서는 미확정이다.** 읽기 전용 DB 복사본에는 제목이 같고 시작일 차이가 7일 이내인 ‘삭제된 시간 지정 일정과 활성 종일 일정’이 서로 다른 ID로 15쌍 있었다. 10쌍은 같은 날짜였다. 이 PC의 옛 15행은 모두 이미 삭제 표시다. 현재도 Google에 남았는지는 같은 계정의 옛 ID를 직접 조회해야 한다. Google Calendar 조회 커넥터는 미연결 상태였고, 이번 진단에서 실계정 조회·동기화·변경은 수행하지 않았다.

## 1. 오너 사례에서 확인한 증거

2026-11-25 11:32:26 KST에 기본 경로의 `deskcal.db`와 SQLite WAL·SHM을 임시 디렉터리에 복사했다. 원본 DB·WAL의 복사 전후 해시와 복사본 해시 일치를 확인했다. 복사본에만 `mode=ro`와 `PRAGMA query_only=ON`을 적용해 일정·충돌·마지막 수신 시각을 집계한 뒤 임시 파일을 지웠다. 자격 증명 파일과 DB의 `setting` 값은 조회하지 않았다.

| 복사본에서 확인한 항목 | 결과 |
| --- | --- |
| 마지막 일정 수신 | 11:30:41 KST. 이 시각은 마지막 성공한 목록 수신을 뜻한다. |
| 일정 행 | 139개 중 활성 99개, 소프트 삭제 40개. 전부 `synced`이고 Google ID와 etag가 있다. dirty·failed와 일정 `sync_conflict`는 각각 0개다. |
| 옛 행과 새 행의 짝 | 예외 회차를 제외하고 같은 비어 있지 않은 제목, 삭제 행과 활성 행, 시작일 차이 7일 이내로 대조했다. 옛 ID 15개와 새 ID 15개가 각각 한 번씩 짝지어졌다. 15쌍 모두 옛 시간 지정 → 새 종일, 10쌍은 같은 날짜, 5쌍은 다른 날짜다. |
| ID와 변경 이력 | 15쌍의 양쪽 모두 각자의 `event_uid = google_event_id`이고 `local_revision=0`이다. 139행 모두 이 DB에는 `created_by='google'`로 들어왔다. 다른 PC나 재구축 이전의 쓰기 이력은 이 DB가 설명하지 못한다. |
| 활성 본체끼리 대조 | 제목이 같은 유일한 한 쌍은 날짜가 하루 다르고 양쪽 모두 `event_uid ≠ google_event_id`였다. 같은 날·같은 제목의 활성 본체 쌍은 0개다. 제목이 바뀐 복제와 다른 캘린더의 항목은 이 검색 밖이다. |
| 반복 | 시리즈 본체 2개, 연결된 예외 3개. 이 복사본에는 Google ID가 없는 예외나 부모 시작의 UTC 시각·종일 여부와 키가 맞지 않는 예외가 없었다. |

**추정.** 15쌍은 시간 지정을 종일로 바꾸는 과정에서 별개 ID가 쓰였을 가능성을 높인다. 제목·기간으로 만든 짝만으로 누가 생성·삭제했는지, 400을 받았는지, 아이폰에 보인 항목이 그중 하나인지 알 수 없다. `deleted_at`은 Google 삭제 응답·취소 수신·전체 목록 누락으로도 설정된다. 진단 우선순위는 이 15쌍에서 오너가 기억하는 한 건을 골라 옛 ID와 보존할 ID를 함께 조회하는 것이다.

## 2. 코드에서 확인한 편집·동기화 경로

| 단계 | 확인한 동작과 코드 위치 |
| --- | --- |
| 앱 UI | 일정 칩·주간 블록·사이드바는 저장된 ID로 연다. 빈 칸은 새 일정 창을 연다. 회차에서 편집 창을 열어도 시리즈 본체를 읽는다. 기존 일정 저장은 `events.Update(SeriesUid(), …)`, 새 일정만 `Create`다. 저장 직후 화면은 새로 그리지만 `ShowEditor`는 빠른 동기화 신호를 보내지 않는다. [MainWindow.xaml.cs:208](../src/DeskCal.App/MainWindow.xaml.cs#L208) · [MainWindow.xaml.cs:237](../src/DeskCal.App/MainWindow.xaml.cs#L237) · [EditorModel.cs:105](../src/DeskCal.App/Editing/EditorModel.cs#L105) · [MainWindow.xaml.cs:249](../src/DeskCal.App/MainWindow.xaml.cs#L249) |
| MCP | `update_event(event_id)`는 필드의 생략과 명시적 null을 구분해 같은 저장소의 `Update`를 호출한다. `occurrence_id`를 쓰면 완료 여부만 변경할 수 있다. 체크리스트·회차 삭제는 별도 도구다. `create_events`는 매 호출마다 새 행을 만든다. 성공한 쓰기 뒤에만 Windows 변경 신호를 보낸다. [CalendarTools.cs:196](../src/DeskCal.Mcp/CalendarTools.cs#L196) · [CalendarTools.cs:181](../src/DeskCal.Mcp/CalendarTools.cs#L181) · [CalendarTools.cs:47](../src/DeskCal.Mcp/CalendarTools.cs#L47) |
| 로컬 식별과 삭제 | 생성은 GUID를 `event_uid`·`google_event_id` 양쪽에 미리 저장한다. 편집은 기존 행을 읽고 `event_id`로 UPDATE한다. `Values`에는 Google ID가 유지되고 `google_etag`는 수정 대상에서 빠진다. 매 쓰기는 `local_revision`을 올리고 dirty/pending을 설정한다. 본체 삭제는 같은 행에 `deleted_at`을 기록한다. [EventRepository.cs:30](../src/DeskCal.Core/EventRepository.cs#L30) · [EventRepository.cs:68](../src/DeskCal.Core/EventRepository.cs#L68) · [EventRepository.cs:104](../src/DeskCal.Core/EventRepository.cs#L104) · [EventRepository.cs:164](../src/DeskCal.Core/EventRepository.cs#L164) · [EventRepository.cs:201](../src/DeskCal.Core/EventRepository.cs#L201) |
| 전송 시점 | 앱 시작 및 2분 주기로 push → pull. MCP 성공 신호는 실행 중인 앱에서 3초간 변경이 잦아든 뒤 `PendingChanges.ExistSince`를 검사한다. 신호를 놓쳐도 정기 동기화가 있다. 30초 `data_version` 검사는 화면을 갱신한다. 앱 프로세스가 종료된 동안에는 MCP가 DB에만 쓰며, 동일 DB와 계정으로 앱을 시작하면 보류 변경을 보낸다. [App.xaml.cs:13](../src/DeskCal.App/App.xaml.cs#L13) · [App.xaml.cs:61](../src/DeskCal.App/App.xaml.cs#L61) · [MainWindow.xaml.cs:88](../src/DeskCal.App/MainWindow.xaml.cs#L88) · [PendingChanges.cs:14](../src/DeskCal.Core/PendingChanges.cs#L14) · [Program.cs:27](../src/DeskCal.Mcp/Program.cs#L27) |
| Google 쓰기 | 기본 캘린더 `primary`만 대상으로 한다. dirty인 본체는 소프트 삭제면 DELETE, 살아 있고 etag가 없으면 사전 할당 ID로 POST, etag가 있으면 같은 ID에 PATCH와 `If-Match`를 보낸다. Calendar 일정에 `events.update`/PUT 호출은 없다. 성공 응답의 ID·etag를 저장하고 전송 중 revision이 달라졌으면 dirty를 남긴다. [EventSync.cs:47](../src/DeskCal.Core/EventSync.cs#L47) · [EventSync.cs:136](../src/DeskCal.Core/EventSync.cs#L136) · [EventSync.cs:268](../src/DeskCal.Core/EventSync.cs#L268) |
| 시간 형식과 400 | 종일은 `start.date`와 배타적 `end.date`, 시간 지정은 `dateTime`과 시간대를 직렬화한다. PATCH에서 반대 형식의 하위 키는 본문에서 사라진다. Google은 생략한 필드를 보존한다. 400 뒤에는 실패 횟수·오류와 dirty가 남아 같은 요청을 재시도한다. 그래도 pull이 성공하면 `last_synced_at`이 갱신되어 앱 하단에 ‘Google 동기화됨’이 뜰 수 있다. 기존 가짜 원격은 `Start`·`End` 객체 전체를 교체해 이 실패를 가렸다. [EventTime.cs:15](../src/DeskCal.Core/EventTime.cs#L15) · [EventSync.cs:249](../src/DeskCal.Core/EventSync.cs#L249) · [EventSync.cs:398](../src/DeskCal.Core/EventSync.cs#L398) · [MainWindow.xaml.cs:172](../src/DeskCal.App/MainWindow.xaml.cs#L172) · [기존 fake, EventSyncTests.cs:468](../tests/DeskCal.Core.Tests/EventSyncTests.cs#L468) |
| 충돌·재시도 | POST 응답 유실 시 동일 ID로 재시도한다. 409면 GET으로 `deskcalUid`를 대조하고, 활성인 자기 이벤트는 etag만 연결해 다음 주기에 PATCH한다. 취소된 자기 이벤트는 같은 ID를 PATCH해 복원한다. PATCH 412는 원격 우선이며 체크리스트가 있으면 항목을 병합한다. DELETE 412는 `Discard`로 가서 삭제 의도가 폐기되고 full pull이 활성 원격을 되살린다. DELETE의 404/410은 이미 삭제된 것으로 처리한다. `Discard`는 `sync_conflict` 행을 쓰고 dirty·etag·syncToken을 비운다. 앱에는 개별 충돌 표시가 없고 MCP 응답은 현재 `push_state`에 기반한다. [EventSync.cs:153](../src/DeskCal.Core/EventSync.cs#L153) · [EventSync.cs:169](../src/DeskCal.Core/EventSync.cs#L169) · [EventSync.cs:201](../src/DeskCal.Core/EventSync.cs#L201) · [EventSync.cs:230](../src/DeskCal.Core/EventSync.cs#L230) · [EventSync.cs:311](../src/DeskCal.Core/EventSync.cs#L311) · [ToolOutput.cs:62](../src/DeskCal.Mcp/ToolOutput.cs#L62) |
| pull·unknown | `events.list(singleEvents=false, showDeleted=true)` 전 페이지를 받고 마지막 토큰을 저장한다. 410이면 전체 대조한다. Google ID 우선, 예외는 부모 ID와 원래 시작 키로도 연결한다. dirty·failed 행은 덮지 않는다. 전체 목록에서 빠진 깨끗한 행에는 로컬 삭제 시각을 기록한다. 일정 상태는 synced/pending/failed뿐이다. `unknown`과 인증 실패 시 pending 복귀는 Google Tasks 생성 경로다. [EventSync.cs:26](../src/DeskCal.Core/EventSync.cs#L26) · [EventSync.cs:85](../src/DeskCal.Core/EventSync.cs#L85) · [EventSync.cs:345](../src/DeskCal.Core/EventSync.cs#L345) · [EventSync.cs:383](../src/DeskCal.Core/EventSync.cs#L383) · [SchemaV1.sql:34](../src/DeskCal.Core/SchemaV1.sql#L34) · [TaskSync.cs:93](../src/DeskCal.Core/TaskSync.cs#L93) |

Google은 PATCH에서 하위 객체의 필드를 병합하고 명시적 JSON `null`로 해당 필드를 지운다고 정의한다. 별도의 공개 실험은 일반 일정에 네 번 요청해 양방향 모두 생략 시 400, 명시적 null 시 200을 관찰했다. 이번 테스트는 실제 deskCal Google SDK가 만든 HTTP 메서드·URL·If-Match·JSON을 로컬 핸들러에서 검사했다. 오너 계정에 요청을 보낸 실험은 아니다. [Google Calendar의 부분 업데이트 규칙](https://developers.google.com/workspace/calendar/api/guides/performance#patch) · [양방향 실계정 대조 실험 원문](https://github.com/n8n-io/n8n/issues/35800) · [Google Events.patch](https://developers.google.com/workspace/calendar/api/v3/reference/events/patch)

## 3. 수정 종류별 재현 판정

가짜 Google에서 11월 9일 09:00–10:00 일정을 먼저 pull한다. UI에서는 기존 일정 편집 창의 종일을 선택하고, MCP의 동등한 입력은 `update_event`에 같은 `event_id`와 `"start_time":null, "end_time":null`을 전달하는 것이다. 테스트는 이 입력이 만드는 저장소 변경을 거쳐 실제 SDK 요청을 캡처했다. `end`에도 아래와 동일한 형식 전환이 필요하며, 이 예시의 종일 종료일은 11월 10일이다.

| 전환 | 캡처한 `start` | 같은 ID·etag에서 성공한 대조군 `start` |
| --- | --- | --- |
| 시간 지정 → 종일 | `{"date":"2026-11-09"}` | `{"date":"2026-11-09","dateTime":null}` |
| 종일 → 시간 지정 | `{"dateTime":"2026-11-09T11:00:00+09:00","timeZone":"Asia/Seoul"}` | 같은 객체에 `"date":null`을 추가 |

‘옛 내용’은 Google에서 이전 제목·시각·색이 계속 보이는지를 뜻한다. ‘두 ID’는 같은 편집 의도에 대해 독립된 활성 Google 본체가 두 개 존재하는 경우다. 아래의 일반 편집은 최초 연결이 정상이고 다음 동기화가 끝난 시점을 기준으로 했다.

| 수정 또는 재현 순서 | 옛 내용 | 두 ID | 근거 |
| --- | --- | --- | --- |
| 단일 제목 수정 | 안 생김 | 안 생김 | UI·MCP 모두 기존 행을 갱신한다. Google에서 가져와 로컬 UID와 원격 ID가 다른 사례도 같은 원격 ID로 PATCH했다. |
| 같은 날 시작·끝 시간 변경 | 안 생김 | 안 생김 | 시간 지정 형식을 유지하면 기존 `dateTime`을 교체한다. |
| 날짜 이동 | 안 생김 | 안 생김 | UI는 기간을 유지해 시작·끝을 같이 옮긴다. MCP도 두 날짜를 함께 보내면 동일하다. |
| MCP에서 `start_date`만 하루 앞당김 | 생김 | 안 생김 | 11/9 종일 → 시작만 11/8이면 보존된 끝 때문에 Google 구간은 `[11/8, 11/10)`이다. 같은 ID가 11/8과 11/9에 보인다. 시작만 기존 종료일 뒤로 옮기면 저장 전에 거절된다. [날짜 대조 테스트](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L141) |
| 시간 지정 ↔ 종일 | **생김** | 편집만 하면 안 생김 | 양방향 모두 기존 ID PATCH가 400. 새 형식은 로컬에 pending/failed로 남고 원격은 옛 형식이다. 그 뒤 별도 `Create`를 호출한 경우에만 이 실험에서 두 ID가 생겼다. [실제 SDK 본문과 재현](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L56) |
| 반복 한 회차의 제목·시각 | 해당 단일 변경 불가 | 안 생김 | MCP `occurrence_id`로는 거절된다. UI 회차 편집 창의 제목·시간 저장은 전체 시리즈 편집이다. 한 회차의 완료·체크리스트는 기존 인스턴스를 PATCH하며, 한 회차 삭제는 그 인스턴스에 `status=cancelled`를 보낸다. [EditorModel.cs:118](../src/DeskCal.App/Editing/EditorModel.cs#L118) · [회차 취소 테스트](../tests/DeskCal.Core.Tests/EventSyncTests.cs#L89) |
| 반복 전체, 예외 없음 | 안 생김 | 안 생김 | 시간 형식을 유지하는 제목·시간·규칙 변경은 본체 ID로 PATCH한다. 종일 전환은 위의 400 조건을 공유한다. |
| 반복 전체, 예외가 있는 상태에서 시작 시각 변경 | 로컬에 생김, Google 잔존은 불명 | 새 본체 안 생김 | 날짜·반복 규칙은 막지만 같은 날 시작 시각과 종일 여부는 검사에서 빠진다. 아래 09시/11시 재현 참조. [EventRepository.cs:89](../src/DeskCal.Core/EventRepository.cs#L89) |
| 카테고리 색 변경·기본색 복귀 | 색 지정은 안 생김, 기본색 복귀는 생김 | 안 생김 | `colorId="8"`은 전송한다. 기본색의 `colorId=null`은 본문에서 생략되어 Google의 옛 색이 남고 로컬은 synced가 된다. 설정의 카테고리 **이름** 변경은 로컬 `category`만 갱신한다. [HTTP 대조](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L18) · [CategoryRepository.cs:36](../src/DeskCal.Core/CategoryRepository.cs#L36) |
| 앱 UI 편집 / MCP `update_event` | 전송 전에는 생김 | 안 생김 | 저장소·전송 분기는 같다. 앱 편집은 통상 다음 정기 주기, MCP는 앱 실행 중 빠른 신호가 작동하면 약 3초 후 보낸다. |
| 앱 종료 중 MCP 수정·삭제 | 재기동 전에는 생김 | 안 생김 | 별도 DB 연결에서 pending을 저장한 뒤 DB를 다시 열고 가짜 동기화를 실행했다. 재기동 뒤 원래 Google ID에 PATCH 또는 DELETE가 한 번 갔다. 인증 실패·400·412는 각각 별도 조건이다. [재기동 대조](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L285) |
| 단일·시리즈 삭제 / 삭제 412 | 성공 시 안 생김, 412면 생김 | 삭제만 하면 안 생김 | 정상은 같은 ID DELETE. 다른 기기가 메모를 바꿔 etag가 달라진 상태에서 삭제하면 원격이 남는다. 여기에 별도 생성까지 하면 **두 ID가 생김**. [삭제 충돌 대조](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L206) |
| 제목 수정 직전 다른 기기에서 메모 수정 | 생김 | 안 생김 | 412 후 원래 제목 한 건만 남고 `sync_conflict`에 로컬 수정이 보관된다. 생성 응답 유실의 409도 같은 ID에 재접속해 다음 회차에 새 내용을 PATCH한다. [412 대조](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L184) · [409 대조](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L163) |

### 반복 일정의 원래 회차 키

회차의 주소는 부모 UID와 `original_start_key`다. 종일은 날짜, 시간 지정은 원래 시작 UTC다. 회차 완료·체크리스트·삭제 시 그 키로 예외 행을 만든다. 첫 전송은 부모 etag가 생긴 뒤 Google 인스턴스를 GET하고, 이미 받은 ID 또는 부모 Google ID와 원래 시작 키로 구성한 인스턴스 ID에 PATCH한다. pull은 `recurringEventId`와 `originalStartTime`을 사용한다. Google에서 한 회차를 옮겨도 `originalStartTime`은 유지된다. [EventRepository.cs:139](../src/DeskCal.Core/EventRepository.cs#L139) · [EventSync.cs:113](../src/DeskCal.Core/EventSync.cs#L113) · [EventSync.cs:469](../src/DeskCal.Core/EventSync.cs#L469) · [Google 반복 인스턴스 식별](https://developers.google.com/workspace/calendar/api/guides/recurringevents)

월요일 09시 회차에 체크리스트를 하나 추가하고 본체 시작을 11시로 옮기면, 전개기가 새 11시 원본과 옛 09시 예외를 모두 표시한다. 체크리스트는 옛 09시에만 있다. 이미 연결된 예외에 Google이 `id/status=cancelled`를 보내면 다음 pull에서 09시가 사라진다. 저장소의 과거 Google 실험에도 시리즈 시각 변경 때 옛 예외를 취소한 기록이 있다. 반대로 미전송 예외에 대한 옛 인스턴스 GET이 404면 `Discard` 뒤 Google ID가 없는 예외가 전체 대조에서 빠져, 가짜 원격에는 본체 하나뿐인데 로컬에는 두 시간이 계속 나타났다. 정상적으로 옮겨진 예외를 날짜가 다르다는 이유로 제거하면 안 된다. [RecurrenceExpander.cs:21](../src/DeskCal.Core/RecurrenceExpander.cs#L21) · [EventSync.cs:340](../src/DeskCal.Core/EventSync.cs#L340) · [두 예외 시나리오](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L237) · [저장소의 M0.5 관측](../docs/design.md#L641)

### 가짜 원격에서 실제로 두 ID가 남은 조건

| 조건 | 확인한 호출과 최종 상태 |
| --- | --- |
| 형식 전환이 실패한 뒤 `Create` | 옛 ID PATCH가 반복해서 400. 계속 수정만 하면 활성 원격 1개. 별도 새 ID로 POST를 한 번 하면 활성 원격 2개, DELETE와 취소 PATCH는 0회. 같은 실패 본문의 `start`·`end`에서 반대 형식만 명시적 null로 바꾼 대조군은 같은 ID로 200, POST 0회. [전환 실패](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L56) · [명시적 null 대조군](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L105) |
| 처음부터 새 일정을 두 번 생성 / 옛 일정 삭제와 새 생성이 독립적으로 실행됨 | 서로 다른 ID로 POST 2회. 삭제 생략 시 DELETE 0회. 옛 ID의 etag가 달라졌다면 DELETE 시도 1회가 412로 끝나고 활성 원격 2개. 재동기화 후에도 유지됐다. [별도 생성 대조](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L206) |
| 최초 또는 토큰 410 직후 **전체 목록 하나에서만** 방금 쓴 ID가 누락됨 | GET으로는 옛 ID가 살아 있는데 로컬이 `deleted_at`을 설정했다. 그 상태에서 별도 생성하면 POST 총 2회, DELETE·취소 PATCH 0회, 활성 원격 2개. 테스트가 목록 누락을 주입했다. 오너의 Google이 실제로 누락했다는 증거는 없다. Tasks에는 `justPushed` 보호가 있고 Calendar에는 없다. [전체 대조 재현](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L323) · [EventSync.cs:383](../src/DeskCal.Core/EventSync.cs#L383) · [TaskSync.cs:285](../src/DeskCal.Core/TaskSync.cs#L285) |

## 4. Builder 수정 방향과 회귀 기준

1. **시간 형식을 바꾸는 PATCH 본문을 먼저 수정한다.** `GoogleCalendarRemote.PatchAsync`에서 실제로 보내는 JSON의 `start`와 `end` 모두에 종일이면 `"dateTime":null`, 시간이면 `"date":null`을 명시한다. 현재 .NET DTO의 C# null은 직렬화에서 빠지므로 HTTP 본문을 검사해야 한다. `ToRemoteTime`의 종일 종료일 배타 규칙과 시간 지정의 RFC3339·시간대를 유지한다. 전송하는 두 시간 객체에만 처리하면 이전 형식을 DB에 추가 저장할 필요가 없다. 체크리스트 충돌 병합처럼 시간 필드 자체가 없는 PATCH는 그대로 둔다. 테스트 대조군은 `ModifyRequest`에서 기존 ID와 If-Match를 유지하며 JSON 본문을 교체해 검증했다. 회귀 기준은 양방향 전환 각각 **기존 ID PATCH 1회, 성공, 활성 원격 1개, POST/DELETE 0회, dirty 해제**다. 색 해제도 명시적으로 요청한 경우에만 최상위 `"colorId":null`을 보낸다. [EventSync.cs:51](../src/DeskCal.Core/EventSync.cs#L51) · [EventSync.cs:183](../src/DeskCal.Core/EventSync.cs#L183) · [본문 대조 코드](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs#L105)
2. **실패와 충돌을 편집한 사람에게 보여 준다.** `MarkFailed`의 `last_error`·`attempt_count`와 미열람 `sync_conflict`를 UI 일정/동기화 표시 및 MCP 조회에 반영한다. “마지막으로 받은 시각”과 “이 수정이 전송되었는지”를 각각 표시한다. 412 처리 중 더 새로운 `local_revision`이 있으면 체크리스트 유무와 관계없이 최신 행의 dirty를 보존한다. 원격 최신본과 폐기한 수정을 보여 주고 사용자가 선택하면 그 ID·최신 etag로 다시 보낸다. 삭제를 강행하거나 INSERT로 전환하지 않는다. 회귀는 수정/삭제 412, 전송 중 재편집, 400과 성공적인 pull이 공존하는 경우다. 기존 상태·충돌 테이블로 구현할 수 있다. [EventSync.cs:296](../src/DeskCal.Core/EventSync.cs#L296) · [SchemaV1.sql:89](../src/DeskCal.Core/SchemaV1.sql#L89)
3. **반복 예외의 키가 바뀌는 편집을 공통 저장소에서 막는다.** `EventRepository.Update`가 정규화한 전후의 시작 날짜·벽시각·종일 여부·시리즈 시간대·반복 규칙을 비교한다. 연결되었거나 `google_recurring_id`로 연결을 기다리는 예외가 하나라도 있고 회차 식별자가 바뀌면 쓰기 전에 이유를 돌려준다. 기존에 남은 예외는 `recurringEventId + originalStartTime`, 취소 상태 및 ID를 읽어 확인하고 체크리스트를 보존한 채 해소한다. 명시적 이전 기능을 설계한다면 옛 키·새 키 매핑과 충돌 결정을 따로 둔다. 회귀는 펜딩/동기화됨/취소 예외, 같은 날 시간 이동, 종일 전환, DST 및 원래 키가 유효한 이동 회차 보존이다. 방지 조건은 스키마 변경 없이 추가할 수 있다. [EventRepository.cs:89](../src/DeskCal.Core/EventRepository.cs#L89) · [RecurrenceExpander.cs:74](../src/DeskCal.Core/RecurrenceExpander.cs#L74)
4. **전체 목록의 누락과 원격 삭제를 구별한다.** `EventSync.PushAsync`에서 성공한 생성·수정 ID를 반환해 `Pull`의 같은 회차 누락 대조에 전달한다. 방금 승인된 ID가 전체 목록에 없을 때는 해당 ID를 GET해 확정하거나 후속 대조 대상으로 남긴다. 네트워크 확인은 SQLite 쓰기 트랜잭션 밖에서 하고 반영 시 revision을 다시 확인한다. 실제 삭제의 404/410·취소 tombstone, 다른 기기의 정상 삭제, 410 후 복구도 회귀에 포함한다. 회차 안에서 ID를 전달하는 변경은 마이그레이션이 필요 없다. [EventSync.cs:85](../src/DeskCal.Core/EventSync.cs#L85) · [TaskSync.cs:76](../src/DeskCal.Core/TaskSync.cs#L76)
5. **호출자가 ‘이동’을 표현할 수 있게 한다.** MCP 도구 설명에 `event_id` 재사용, 날짜 이동 시 유지할 기간을 계산해 양 끝을 보내는 예시, 실패/보류 결과를 조회하는 방법을 명시한다. `update_event`의 생략 필드 보존 계약과 새 일정의 독립 ID를 유지한다. UI는 성공적인 저장·삭제 뒤 같은 3초 동기화 예약을 재사용할 수 있다. 종료 중 MCP 쓰기는 ‘앱을 열면 전송’ 상태로 표시할 수 있다. 이번 우선 수정에 DB 마이그레이션이나 기존 일정 일괄 재생성은 필요 없다. [ToolCatalog.cs:32](../src/DeskCal.Mcp/ToolCatalog.cs#L32) · [MainWindow.xaml.cs:249](../src/DeskCal.App/MainWindow.xaml.cs#L249)

## 5. 고아 일정 드라이런과 아이폰 판별

1. **대상 한 쌍을 식별한다.** 오너가 본 옛 날짜·시각 및 RFC3339 오프셋, 새 종일 날짜, Google 계정·캘린더 이름을 확인한다. 다른 PC가 같은 캘린더를 사용하면 해당 PC의 보류 변경과 충돌도 비교한다. DB·WAL의 일관된 임시 복사본을 읽기 전용으로 열고 아래 열을 추출한다. 삭제 행도 보존한다.

   ```sql
   SELECT event_id, event_uid, google_event_id, google_etag, parent_event_id,
          google_recurring_id, original_start_key, recurrence_rule, status,
          deleted_at, is_dirty, push_state, local_revision, created_by,
          title, google_start_raw, google_end_raw
   FROM event;
   ```

2. **Google에 실제로 무엇이 있는지 읽는다.** 같은 계정의 `primary`에서 옛 ID와 새 ID를 각각 `events.get`한다. 전체 드라이런은 읽기 전용 권한 `calendar.events.readonly`로 `events.list(singleEvents=false, showDeleted=true)`를 끝 페이지까지 순회한다. 진단용 전체 조회에 앱의 syncToken이나 날짜·제목 필터를 쓰지 않는다. 최소 비교 필드는 `id, etag, status, summary, start, end, updated, recurringEventId, originalStartTime, iCalUID, extendedProperties.private`다. 반복은 별도로 옛날·새날을 포함하는 구간의 `events.instances(showDeleted=true)`에서 실제 회차와 취소 회차를 대조한다. [Google Events.list](https://developers.google.com/workspace/calendar/api/v3/reference/events/list) · [Google Events.instances](https://developers.google.com/workspace/calendar/api/v3/reference/events/instances)
3. **각 후보에 근거를 붙인다.** `(calendarId, Google event id)`로 먼저 대응시킨다. 원격 활성이고 로컬 부재/삭제인 행, 동일한 `deskcalUid`가 여러 활성 본체 ID에 걸친 행, 서로 다른 UID이지만 오너가 동일 수정이라고 식별한 쌍을 분리한다. pull이 옛 ID까지 이미 로컬에 재수입할 수 있어 단순 집합 차이로는 놓친다. `deskcalUid`는 Google 출처 일정을 deskCal에서 편집해도 붙고, 둘을 따로 생성하면 각기 다르다. `created_by='google'`도 이 PC로 들어온 경로만 말해 준다. `privateExtendedProperty=deskcalUid=<알려진 값>`은 정확한 값 필터다. 키 존재 전체 검색은 목록을 읽어 로컬에서 검사한다. `iCalUID`는 교차 캘린더 참고용으로, 한 시리즈의 회차들이 공유한다. 현재 DB에는 iCalUID 열이 없고, 구현은 `deskcalGen`을 전송하지 않는다. `status=cancelled`, 정상 이동·취소 예외, 다른 캘린더/주최자, 다른 PC의 보류 변경은 자동 제거 후보에서 제외한다. [EventSync.cs:242](../src/DeskCal.Core/EventSync.cs#L242) · [EventSync.cs:435](../src/DeskCal.Core/EventSync.cs#L435) · [Google 비공개 속성](https://developers.google.com/workspace/calendar/api/guides/extended-properties) · [Google 이벤트 리소스와 iCalUID](https://developers.google.com/workspace/calendar/api/v3/reference/events)
4. **드라이런을 오너에게 보여 준다.** 캘린더, 삭제 검토 ID와 보존 ID, 제목, 각각의 옛·새 시작/끝과 시간대, 본체/회차, 현재 상태·etag, 로컬 삭제/보류, 후보 근거를 한 줄에 넣는다. Google `cancelled` 또는 접근 권한을 확인한 동일 캘린더의 404/410은 ‘제거할 활성 원본 없음’, 403·일시 오류는 ‘판정 보류’로 표시한다. 제목 유사도만으로는 삭제를 선택하지 않는다. 이번 로컬 15쌍의 기본 표시는 ‘서버 확인 대기’다.
5. **승인받은 개별 ID만 정리한다.** 오너가 삭제 대상 ID와 보존 ID를 명시적으로 승인한 후에만 별도 작업으로 실행한다. 직전에 두 ID를 다시 GET하고 캘린더·본체/회차 범위·상태·etag가 미리보기와 같은지 확인한다. 승인된 옛 ID에만 `If-Match`로 DELETE한다. 412면 중단하고 갱신된 미리보기를 제시한다. 시리즈 전체 삭제와 한 회차 취소는 별도 승인 범위다. 실행 후 옛 ID의 취소/부재와 보존 ID의 활성 상태를 다시 읽어 기록한다. [Google 리소스 버전 확인](https://developers.google.com/workspace/calendar/api/guides/version-resources) · [Google Events.delete](https://developers.google.com/workspace/calendar/api/v3/reference/events/delete)

| 같은 Google 기본 캘린더의 웹·API 결과 | 아이폰에서 내릴 판단 |
| --- | --- |
| 서로 다른 활성 옛 ID와 새 ID가 모두 있음 | 서버에 두 리소스가 있다. deskCal 출처 표식·관련 PC의 호출 이력으로 생성 주체를 판별한다. 아이폰 캐시만으로는 설명되지 않는다. |
| 옛 ID만 활성, 새 형식·시간은 deskCal에만 있음 | 로컬 `failed/last_error`, pending, `sync_conflict`를 확인한다. 시간↔종일의 400, 앱 종료·인증·지연, 412를 먼저 구분한다. 같은 ID의 기간이 두 날짜에 걸친 경우도 확인한다. |
| 새 일정만 활성이고 옛 ID는 취소/부재 | 아이폰 캘린더 목록에서 해당 Google 캘린더만 선택한다. iCloud·다른 Google 계정·구독 캘린더를 가렸을 때 사라지면 다른 캘린더의 복제다. 같은 캘린더에서만 옛 항목이 보이면 새로고침 후 다시 확인한다. 이 조건은 아이폰의 표시 캐시·가져오기 지연과 맞는다. [Apple의 캘린더별 표시 방법](https://support.apple.com/ko-kr/guide/iphone/-iph3d1110d4/ios) |

## 6. 검증과 작업 범위

기준 브랜치는 `GMiiiN/deskcal-orphan-events`, 제품 소스 기준 커밋은 `97ce8aba729c5275fae9868f983b47be7d436098`, 제품 버전은 0.6.0이다. 변경 파일은 이 보고서, 신규 진단 테스트, 기존 가짜 원격의 호출 기록·응답 조건뿐이다. 보고서를 포함한 로컬 커밋은 `git log -1 --format=%h -- reports/deskcal-orphan-events.md`로 식별할 수 있다.

Windows, .NET SDK 10.0.404에서 **신규 진단 16건 통과**. 관련 회귀는 **Core 99 + App 편집 모델 22 + MCP 입력 거절 1 = 122건 통과, 실패 0건**이다. 16건은 122건에 포함된다. 신규 테스트는 현재 결함을 보이는 단언도 포함하므로 수정 시 해당 단언을 위의 성공 기준으로 전환한다. 테스트 대상은 임시 SQLite, 가짜 원격 및 메모리 HTTP 핸들러다. MCP 테스트는 임시 DB에서 성공 쓰기 신호를 발생시키지 않는 입력 거절 사례를 선택했다. [진단 테스트](../tests/DeskCal.Core.Tests/OrphanEventDiagnosisTests.cs) · [기존 가짜 원격](../tests/DeskCal.Core.Tests/EventSyncTests.cs#L376) · [MCP 입력 거절 범위](../tests/DeskCal.Mcp.Tests/McpToolTests.cs#L384)

```powershell
$env:PATH = 'C:/Users/USER/.dotnet;' + $env:PATH
dotnet test tests/DeskCal.Core.Tests/DeskCal.Core.Tests.csproj --no-restore --filter 'FullyQualifiedName~OrphanEventDiagnosisTests' --logger 'console;verbosity=normal'
dotnet test DeskCal.sln --no-restore --filter 'FullyQualifiedName~OrphanEventDiagnosisTests|FullyQualifiedName~EventSyncTests|FullyQualifiedName~RecurrenceTests|FullyQualifiedName~RepositoryTests|FullyQualifiedName~PendingChangesTests|FullyQualifiedName~TaskSyncTests|FullyQualifiedName~EditorModelTests|FullyQualifiedName~McpToolTests.OversizedBatchesAndConflictingIdsCannotWrite' --verbosity quiet
```
