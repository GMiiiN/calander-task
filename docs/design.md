# DeskCal 설계 문서

> 작성: Claude (Opus) · 2026-10-30 · 상태: v3.1 (요구사항·디자인 확정, Codex v3 검증 18건 반영)
> 이력: v1 초안 → v2 Codex 검증 10건 → v3 요구사항 확정 → v3.1 Codex v3 검증 15+3건 + 디자인 확정

Windows 바탕화면에 붙어 있는 달력 앱. 노트북과 데스크톱에서 같은 데이터를 쓰고(Google 동기화), Claude에게 "다음 주 화요일 3시 치과 넣어줘", "이번 주 일정 정리해 줘", "다음 주에 2시간 비는 날 찾아줘"라고 말하면 MCP 도구로 처리된다.

사용자가 쓰던 기존 바탕화면 달력 앱의 사용 경험을 비교 기준으로 삼았다. 다른 앱의 코드·리소스는 쓰지 않고 모두 새로 구현한다. 기존 앱에 대한 일반적인 관찰은 "관찰", 이 앱의 판단은 "결정"으로 구분한다.

---

## 0. 요구사항 (2026-10-30 사용자 확정)

### 기능
| 영역 | 요구 |
|---|---|
| 일정 | 추가·수정·삭제, **반복 일정**, 하루 종일/시간 일정, 여러 날 일정, **카테고리별 색상** |
| 할 일 | **체크리스트**, 마감일은 선택(있으면 달력의 그 날짜에도 표시) |
| D-day | 시험·기념일 등까지 남은 날 표시 |
| 화면 | **월간/주간 토글**, 오른쪽 **할 일 사이드바**(미완료 할 일 전체 + 오늘부터 14일 일정, 둘 다 체크 가능), **검색** |
| Claude | 일정·할 일(메모 포함) 추가·수정·삭제·조회, **여러 개 한 번에 등록**, **빈 시간 찾기**, **일정 브리핑/요약** |
| 동기화 | **노트북 + 데스크톱**에서 같은 데이터. 전부 Google로: 일정·D-day → Google 기본 캘린더, 할 일 → Google Tasks 기본 목록 |

### 결정된 조건
- 스택: C# / WPF (.NET 10). C# 코드는 .NET 표준 명명 규칙, DB는 snake_case.
- 시각 디자인: Figma (§6.1). 월간 카드 기본 디자인은 사용자 작업, 나머지 화면은 같은 톤으로 Claude가 작성 후 사용자 확정.
- 선택하지 않은 기능: 알림(리마인더), 음력 날짜 표시, 별도 스티커 메모, Outlook/iCloud. "메모"는 일정·할 일의 메모 필드를 뜻한다.

---

## 1. 기술 스택

| 기준 | C# / WPF (.NET 10) | Electron (TS) | Tauri (Rust + Web) |
|---|---|---|---|
| 바탕화면 고정 (Win32 창 제어) | P/Invoke로 직접. `HwndSource`로 메시지 훅 | 네이티브 애드온 필요 | `windows` crate + 웹뷰 연동 |
| MCP SDK | 공식 C# SDK | 공식 TS SDK | 공식 Rust SDK |
| Google API | 공식 .NET 클라이언트 | 공식 Node 클라이언트 | 비공식 |
| 상주 메모리 | 낮음 | 높음 (Chromium) | 낮음 |

**결정: C# / WPF.** 핵심 난제(바탕화면 고정)가 순수 Win32 작업이고, 상주 앱이라 메모리가 중요하며, MCP·Google 모두 공식 .NET 라이브러리가 있다.

| 용도 | 패키지 |
|---|---|
| MCP 서버 | `ModelContextProtocol` (stdio, `Microsoft.Extensions.Hosting`) |
| DB | `Microsoft.Data.Sqlite` (ORM 없이 ADO.NET) |
| 반복 규칙 전개 | `Ical.Net` (RFC 5545 전체 — Google에서 들어오는 임의 규칙 처리) |
| Google | `Google.Apis.Calendar.v3`, `Google.Apis.Tasks.v1`, `Google.Apis.Drive.v3`, `Google.Apis.Auth` |
| MVVM 보조 | `CommunityToolkit.Mvvm` |
| 테스트 | xUnit |

---

## 2. 아키텍처

### 2.1 구성

```
           Google (진실의 원천)
   ┌───────────────┬───────────────┬─────────────────────┐
   │ Calendar      │ Tasks         │ Drive appDataFolder │
   │ primary 캘린더 │ 기본 할 일 목록 │ settings.json        │
   │ + 한국 휴일    │               │ (카테고리 이름 등)    │
   └──────┬────────┴───────┬───────┴──────────┬──────────┘
          │ 동기화 (SyncEngine, PC마다)         │
 ┌────────┴────────────────┴──────────────────┴───────┐
 │ 노트북 / 데스크톱 각각                                │
 │  Claude Code/Desktop ─stdio─▶ DeskCal.Mcp.exe ─┐     │
 │                                               ├─▶ DeskCal.Core ─▶ deskcal.db (SQLite, WAL)
 │                         DeskCal.exe (WPF) ────┘     │
 │  변경 알림: named event "Local\DeskCal.DataChanged"   │
 └─────────────────────────────────────────────────────┘
```

- **Google이 진실의 원천**이고, 각 PC의 SQLite는 오프라인에서도 동작하는 로컬 복제본 + 미전송 변경(outbox)이다.
- 앱과 MCP 서버는 모두 `DeskCal.Core`를 통해 로컬 DB에 쓴다. 쓰기 규칙(검증, 타임스탬프, 소프트 삭제, dirty·revision 관리)은 Core 한 곳에 있다.
- 동기화도 Core의 `SyncEngine`이 한다. 앱·MCP 어느 쪽에서든 실행될 수 있고, named mutex `Local\DeskCal.Sync`로 동시에 하나만 돈다.

### 2.2 관찰: 기존 앱
- AI 연동 도구가 실행 중인 앱 본체에 요청을 넘겨 처리하는 구조라, **앱이 꺼져 있으면 AI 연동이 동작하지 않는다**.
- 외부 캘린더 데이터는 로컬 일정과 별도로 보관했다가 화면에서 합쳐 보여준다.

### 2.3 결정: MCP는 로컬 DB에 직접, 동기화는 Core가
| 방식 | 장점 | 단점 |
|---|---|---|
| A. 앱 경유 (기존 앱 방식) | 쓰기 경로 하나 | 앱이 꺼져 있으면 Claude 연동 불가 |
| **B. DB 직접 + 변경 알림 (채택)** | 앱 없이도 Claude 사용 가능 | 프로세스 간 동시성 설정 필요 |

- shadow 테이블은 두지 않는다. 로컬 일정 = Google 일정이므로 한 테이블에 Google ID·etag·원문 시각을 같이 둔다 (§4). 원천이 하나라 병합 계층이 필요 없다.

동시성 설정 (양쪽 프로세스 공통):
```sql
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;
PRAGMA foreign_keys = ON;
PRAGMA synchronous = NORMAL;
```
- 연결 문자열 `Default Timeout=5` — `Microsoft.Data.Sqlite`는 `SQLITE_BUSY`를 `CommandTimeout`까지 자체 재시도하므로 함께 맞춘다.
- 쓰기 트랜잭션은 `BEGIN IMMEDIATE`. WAL은 경합을 없애지 않고 직렬화할 뿐이며, 테스트로 확인한다 (§9).
- **HTTP 요청 중에는 DB 쓰기 트랜잭션을 잡지 않는다** (§5.2).
- 같은 PC 안의 앱 편집 팝업 vs MCP 수정 충돌: 마지막 저장 우선. 팝업은 저장 직전 대상 삭제 여부만 확인해 알린다.

변경 알림 (같은 PC 안):
- 로컬 DB에 쓴 쪽(MCP 또는 SyncEngine)이 커밋 후 `EventWaitHandle("Local\\DeskCal.DataChanged")`를 `Set()` → 앱이 현재 화면을 재조회.
- 앱 자신의 UI 쓰기 후에는 직접 재조회.
- 신호 유실 대비: 감시 전용 장수명 연결에서 `PRAGMA data_version`을 창 활성화 시와 30초마다 비교 (같은 연결에서의 비교만 의미가 있으므로 연결 재생성 시 기준값 리셋).

### 2.4 동기화 실행 시점
| 주체 | 시점 |
|---|---|
| 앱 | 시작 시, 2분마다, 로컬 쓰기 직후(디바운스 3초), 창 활성화 시 |
| MCP 서버 | 쓰기 도구 직후 push 시도, 조회 도구 전 마지막 동기화가 60초보다 오래됐으면 pull |

- MCP의 동기화 예산은 **mutex 대기를 포함해 10초**. mutex를 못 잡거나 예산을 넘으면 동기화를 건너뛰고 로컬 결과만 반환한다(`synced: false`). mutex는 획득한 스레드에서 해제한다(`using`).
- MCP는 로그인하지 않는다. 앱에서 로그인한 적이 없으면 로컬에만 쓰고 `synced: false, reason: "not_signed_in"`.
- 오프라인이면 dirty 행이 쌓였다가 다음 동기화 때 전송된다.
- Google은 데스크톱 앱에 푸시 알림을 줄 수 없으므로(웹훅은 공개 HTTPS 주소 필요) 폴링한다.

### 2.5 솔루션 구조
```
calander-task/
├─ DeskCal.sln
├─ src/
│  ├─ DeskCal.Core/        # 도메인, DB, 반복 전개, SyncEngine, Google 어댑터 — UI 의존 없음
│  ├─ DeskCal.App/         # WPF (바탕화면 창, 월간/주간, 사이드바, 트레이)
│  └─ DeskCal.Mcp/         # MCP stdio 서버
├─ tests/
│  └─ DeskCal.Core.Tests/
└─ docs/design.md
```

---

## 3. 바탕화면 고정

### 3.1 관찰: 기존 앱
- 바탕화면 셸 프로세스 안에서 직접 그리는 방식으로 보인다. 아이콘과 겹친 영역에서는 달력이 클릭을 받고, 편집은 더블클릭으로만 열린다(사용자 경험).
- 셸 프로세스 안에서 동작하는 방식은 셸과 운명을 같이하고, 다른 바탕화면 꾸미기 프로그램과 충돌할 여지가 있다.

### 3.2 후보
| 방식 | 클릭/편집 | 안정성 | 난이도 |
|---|---|---|---|
| A. 셸 프로세스 안에서 그리기 | 가능 | 낮음 (셸 장애 전파, 보안 소프트웨어 오탐) | 높음 |
| B. WorkerW 아래 배치 | **불가** (아이콘 레이어가 입력을 가져감) | 중간 | 중간 |
| **C. 최하단 고정 일반 창 (채택)** | 가능 | 높음 | 낮음~중간 |

### 3.3 결정: C
1. 창: `WindowStyle=None`, `AllowsTransparency=True`, `ShowInTaskbar=False`, 확장 스타일 `WS_EX_TOOLWINDOW` + `WS_EX_NOACTIVATE`(편집 시 해제). `WS_EX_NOACTIVATE`는 활성화 방지일 뿐 하단 고정 수단이 아니다.
2. Z순서 상태 머신 (`HwndSource.AddHook`으로 `WM_WINDOWPOSCHANGING` 처리):

   | 상태 | 진입 조건 | 처리 |
   |---|---|---|
   | `Normal` | 기본 / 다른 앱이 전경 | `hwndInsertAfter`를 `HWND_BOTTOM`으로 덮어씀 |
   | `ShowDesktop` | 전경 창이 `WorkerW`/`Progman` | 개입 안 함. 진입 시 `SetWindowPos(HWND_TOP, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)` 1회 |
   | `Editing` | 편집 팝업·검색창 열림 | 개입 안 함 |

   `SWP_NOZORDER` 호출은 손대지 않는다.
3. Win+D 감지: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)`를 UI 스레드에서 등록, 콜백 델리게이트는 필드로 보관(GC 방지).
   - **M-1 실측 (Windows 11, 2026-10-30)**: Win+D 시 전경은 `WorkerW`가 아니라 **`Progman`**이 되고, Progman이 일반 창들 위로 올라온다. `HWND_TOP`으로 올리는 것은 시차를 두고 반복해도 Progman을 넘지 못했다.
   - 채택 방식(실험 A): 바탕화면 보기 동안에만 `HWND_TOPMOST`, 벗어나면 `HWND_NOTOPMOST` + `HWND_BOTTOM`. 보강 3가지:
     1. WPF가 `ShowInTaskbar=False` 창에 붙이는 **숨은 owner 창을 끊는다**(`GWLP_HWNDPARENT = 0`).
     2. 바탕화면 보기 중 외부에서 오는 Z순서 강등은 `WM_WINDOWPOSCHANGING`에서 거부한다. 우리 호출은 플래그로 구분한다 — 훅에는 `HWND_TOPMOST`가 아니라 실제 삽입 위치 핸들이 오기 때문.
     3. 250ms 타이머로 전경과 `WS_EX_TOPMOST`를 재확인해 다시 건다. 작업 표시줄·시작·검색 등 셸 요소가 전경이 되어도 바탕화면 보기는 유지한다.
   - 결과: Win+D 5회 × 3번 시험에서 **2회차 이후는 매번 성공, 앱 실행 후 첫 Win+D만 실패**(TOPMOST가 우리 창의 `WM_WINDOWPOSCHANGING`을 거치지 않고 2초 내내 해제됨). 시작 시 TOPMOST 예열은 효과 없음. **미해결 — 원인 조사 필요.**
   - 기각(실험 B): Progman을 owner로 지정 → 우리 창이 다른 앱 위로 올라오고 전경을 가져가 Win+D 자체가 동작하지 않았다.
4. **조정 모드** (2026-10-30 확정): 평소에는 크기·위치가 잠겨 있고, `⋯` 메뉴/트레이의 "크기·위치 조정"을 켠 동안에만 카드 안쪽 가장자리 10px를 끌어 크기를, 안쪽을 끌어 위치를 바꾼다. 최소 크기 760 × 480.
   - 손잡이는 WPF `Thumb`(조정 모드에서만 표시)로 직접 처리한다. 투명 layered 창은 시스템 크기 조절 테두리가 없어 `WM_NCHITTEST`로 `HTBOTTOMRIGHT`를 돌려줘도 크기가 바뀌지 않았다(M-1 실측). 카드 바깥 그림자 여백은 투명해 입력을 받지 못하므로 손잡이는 카드 안쪽에 둔다.
   - 조정 중에는 달력을 다른 앱 위로 꺼낸다(사용자가 보면서 조정해야 하므로). 완료하면 다시 바탕화면 위치로.
5. **크기와 위치** (2026-10-30 확정):
   - 기본 크기 약 **892 × 559** (달력 640 + 간격 12 + 사이드바 240, DIP). 바탕화면 아이콘을 가리지 않도록 사용자 화면의 빈 영역 안에 들어가는 크기로 정했다.
   - 첫 실행 시 주 모니터 작업 영역 가운데에 둔다. WPF `CenterScreen`은 다른 모니터에 띄울 수 있어(M-1 실측) `SystemParameters.WorkArea`로 직접 계산한다. 아이콘 배치는 사람마다 달라 자동 회피는 하지 않는다.
   - 트레이 "위치 조정"에서만 이동·크기 변경 가능(기본 잠금). 크기·위치는 PC·모니터별로 로컬 `setting`에 저장(동기화 안 함 — 노트북과 데스크톱 화면이 다르므로).
   - DPI PerMonitorV2.

M-1 spike 체크리스트 (Windows 10 22H2 / Windows 11 23H2·24H2, **노트북·데스크톱 둘 다**):
- Win+D 1회 → 보임 / 2회 → 다시 최하단
- 다른 앱 활성화 시 그 아래로 내려감
- 편집 팝업 포커스 중 Z순서 안정
- Explorer 재시작 후 동작
- 전체화면 앱 실행/종료 후 최하단 유지
- 다중 모니터·DPI 다른 모니터 간 이동 (데스크톱)
- 배경 투명도 50%(배경 흐림)가 바탕화면 위에서 실제로 비치는지

---

## 4. 데이터 모델과 저장

### 4.1 Google 매핑
| DeskCal 개념 | Google 저장 위치 | 비고 |
|---|---|---|
| 일정 | Calendar `primary`의 이벤트 | 휴대폰에서 넣은 일정도 그대로 보임 |
| 반복 일정 | 이벤트 `recurrence` (RRULE/EXDATE) + 원본 `start.timeZone` | 전개는 Ical.Net, 원본 시간대 기준 |
| 반복의 특정 회차 변경·취소 | Google 예외 인스턴스 (`recurringEventId` + `originalStartTime`) | Google 모델 그대로 |
| 일정 완료 체크 | `extendedProperties.private.deskcalDone = "1"` | Google에는 일정 완료 개념이 없음 |
| D-day | 이벤트 + `extendedProperties.private.deskcalDday = "1"` | |
| 로컬 식별·재생성 세대 | `extendedProperties.private.deskcalUid`, `deskcalGen` | 409/복구 시 "우리가 만든 것인지" 확인용 |
| 카테고리 | 이벤트 `colorId` | 카테고리 = Google 이벤트 색. 색 없음 = "기본" |
| 카테고리 이름·주 시작 요일 | Drive `appDataFolder/settings.json` | 앱 전용 숨김 저장소. 두 PC가 같은 값을 봄 |
| 할 일 | Tasks 기본 목록(`@default`) | `due`는 **날짜만** 의미 있음 |
| 공휴일 | 공개 휴일 캘린더 (ID는 M0.5에서 확인) | 대체·임시공휴일까지 Google이 관리 |

- 카테고리 = Google 색: Google 데이터만으로 두 PC가 일치하고 휴대폰 앱에서도 같은 색으로 보인다. 색 목록은 하드코딩하지 않고 `colors.get`으로 받는다(현재 11개지만 개수는 API 계약이 아님). 디자인은 이 팔레트를 쓴다 (§6).
- 할 일에는 카테고리가 없다 (Google Tasks에 색 필드 없음).

### 4.2 스키마 (v1 — M0.5 spike 결과로 확정)
DB: `%LOCALAPPDATA%\DeskCal\deskcal.db`. 마이그레이션은 `PRAGMA user_version` + 순차 함수, `BEGIN IMMEDIATE` 후 `user_version` 재확인 → DDL·버전 갱신을 한 트랜잭션에서. DB 버전이 프로그램이 아는 최신보다 크면 명시적 오류.

```sql
CREATE TABLE event (
    event_id              INTEGER PRIMARY KEY,
    event_uid             TEXT    NOT NULL UNIQUE,    -- 하이픈 없는 소문자 hex 32자. MCP에 노출되는 ID
    google_event_id       TEXT    UNIQUE,             -- 로컬 생성이면 insert 전에 미리 정해 저장 (§5.2)
    google_etag           TEXT,
    google_gen            INTEGER NOT NULL DEFAULT 0, -- 복구 재생성 세대
    parent_event_id       INTEGER REFERENCES event (event_id) ON DELETE CASCADE,
    google_recurring_id   TEXT,                       -- 최초 동기화에서 부모보다 예외가 먼저 와도 저장, 나중에 parent_event_id로 해석
    original_start_key    TEXT,                       -- 예외 회차의 원래 회차 키: 종일 'YYYY-MM-DD', 시간 'YYYY-MM-DDTHH:MM:SSZ'(UTC)
    status                TEXT    NOT NULL DEFAULT 'confirmed' CHECK (status IN ('confirmed', 'tentative', 'cancelled')),
    title                 TEXT    NOT NULL DEFAULT '',
    memo                  TEXT    NOT NULL DEFAULT '',  -- Google description
    -- 원문(진실): Google 표현 그대로
    google_start_raw      TEXT,                       -- start.date 또는 start.dateTime 원문
    google_end_raw        TEXT,
    time_zone             TEXT,                       -- IANA. 시간 일정·반복 전개의 기준
    -- 표시용 파생 캐시(로컬 시간대): 원문에서 계산, 범위 조회 인덱스용
    start_date            TEXT    NOT NULL,           -- 'YYYY-MM-DD'
    end_date              TEXT    NOT NULL,           -- inclusive
    start_time            TEXT,                       -- 'HH:MM', NULL=종일
    end_time              TEXT,
    recurrence_rule       TEXT,                       -- Google recurrence 배열 원문(개행 구분)
    color_id              TEXT,                       -- 카테고리, NULL=기본
    is_busy               INTEGER NOT NULL DEFAULT 1, -- Google transparency: opaque=1, transparent=0
    is_done               INTEGER NOT NULL DEFAULT 0,
    is_dday               INTEGER NOT NULL DEFAULT 0,
    created_by            TEXT    NOT NULL DEFAULT 'user' CHECK (created_by IN ('user', 'claude', 'google')),
    created_at            TEXT    NOT NULL,
    updated_at            TEXT    NOT NULL,
    deleted_at            TEXT,
    -- outbox
    local_revision        INTEGER NOT NULL DEFAULT 0, -- 로컬 쓰기마다 +1
    is_dirty              INTEGER NOT NULL DEFAULT 0,
    push_state            TEXT    NOT NULL DEFAULT 'synced' CHECK (push_state IN ('synced', 'pending', 'failed')),
    attempt_count         INTEGER NOT NULL DEFAULT 0,
    last_error            TEXT,
    CHECK (end_date >= start_date),
    CHECK (end_time IS NULL OR start_time IS NOT NULL)
);
CREATE UNIQUE INDEX ux_event_occurrence ON event (parent_event_id, original_start_key) WHERE parent_event_id IS NOT NULL;
CREATE INDEX ix_event_range ON event (start_date, end_date) WHERE deleted_at IS NULL AND status <> 'cancelled';
CREATE INDEX ix_event_dirty ON event (is_dirty) WHERE is_dirty = 1;

CREATE TABLE todo (
    todo_id         INTEGER PRIMARY KEY,
    todo_uid        TEXT    NOT NULL UNIQUE,
    google_task_id  TEXT    UNIQUE,
    google_etag     TEXT,
    title           TEXT    NOT NULL DEFAULT '',
    memo            TEXT    NOT NULL DEFAULT '',      -- Google notes
    due_date        TEXT,
    is_done         INTEGER NOT NULL DEFAULT 0,
    completed_at    TEXT,
    position        TEXT,
    created_by      TEXT    NOT NULL DEFAULT 'user' CHECK (created_by IN ('user', 'claude', 'google')),
    created_at      TEXT    NOT NULL,
    updated_at      TEXT    NOT NULL,
    deleted_at      TEXT,
    local_revision  INTEGER NOT NULL DEFAULT 0,
    is_dirty        INTEGER NOT NULL DEFAULT 0,
    push_state      TEXT    NOT NULL DEFAULT 'synced' CHECK (push_state IN ('synced', 'pending', 'failed', 'unknown')),
    attempt_count   INTEGER NOT NULL DEFAULT 0,
    last_error      TEXT
);
CREATE INDEX ix_todo_open ON todo (is_done, due_date) WHERE deleted_at IS NULL;

CREATE TABLE category (
    color_id        TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    color_hex       TEXT NOT NULL                     -- colors.get 결과 캐시
);

CREATE TABLE holiday (
    holiday_date    TEXT NOT NULL,
    name            TEXT NOT NULL,
    is_day_off      INTEGER NOT NULL DEFAULT 1,       -- 공휴일/기념일 구분 (M0.5에서 판별 규칙 확정)
    PRIMARY KEY (holiday_date, name)
);

CREATE TABLE sync_state (
    sync_key        TEXT PRIMARY KEY,                 -- 'calendar_primary', 'tasks_default', 'holidays', 'settings'
    sync_token      TEXT,                             -- Calendar nextSyncToken
    remote_id       TEXT,                             -- settings: Drive fileId
    local_revision  INTEGER NOT NULL DEFAULT 0,       -- settings 문서 revision
    is_dirty        INTEGER NOT NULL DEFAULT 0,
    last_synced_at  TEXT
);

CREATE TABLE sync_conflict (                          -- 원격 우선으로 폐기된 로컬 변경 기록 (사용자 알림용)
    conflict_id     INTEGER PRIMARY KEY,
    item_kind       TEXT NOT NULL,                    -- 'event' | 'todo' | 'settings'
    item_uid        TEXT NOT NULL,
    discarded_json  TEXT NOT NULL,
    occurred_at     TEXT NOT NULL,
    is_seen         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE setting (                                -- 이 PC 전용 (창 위치·크기, 투명도, 보던 보기, Google 계정 ID). 동기화 안 함
    setting_key     TEXT PRIMARY KEY,
    setting_value   TEXT NOT NULL
);
```

Core 검증 계약 (로컬 생성·수정 시):
- 제목 비어 있으면 거부 (Google에서 온 제목 없는 일정은 허용).
- 날짜는 실재하는 `YYYY-MM-DD`, 시각은 `00:00`–`23:59`.
- 같은 날이면 `end_time > start_time`. 자정 넘김은 `end_date`를 다음 날로.
- `start_time`만 주어지면 `end_time = start_time + 1시간` (자정을 넘으면 `end_date` +1일). 저장 전에 정규화하므로 CHECK와 충돌하지 않는다.

### 4.3 시간대
- **원문이 진실, 로컬 필드는 파생 캐시.** Google `start`/`end`의 `date`·`dateTime` 원문과 `timeZone`을 그대로 보존한다(초 단위 포함). 로컬 `start_date`/`start_time` 등은 이 PC의 시간대로 변환한 표시용 값이다.
- 반복 일정은 **원본 시간대로 전개한 뒤** 각 회차를 로컬로 변환한다. 예: LA 09시 매주 일정은 DST 전후로 한국 시각이 달라지는 것이 정상이며, 시작일의 한국 시각으로 고정하면 틀린다.
- 종일 일정: Google `end.date`는 배타적 → 파생 `end_date` = -1일.
- push: **바뀐 필드만** 보낸다. 시각을 바꾸지 않은 수정(제목·완료 등)에는 `start`/`end`/`timeZone`을 보내지 않는다. 로컬에서 새로 만들거나 시각을 바꿀 때만 로컬 시각 + 시스템 IANA 시간대(`TimeZoneInfo.TryConvertWindowsIdToIanaId`)를 보낸다.

### 4.4 반복 일정
- 저장: Google `recurrence` 원문 + 원본 시간대. 휴대폰에서 만든 복잡한 규칙도 손실 없이 보존.
- 전개 순서:
  1. 시리즈를 원본 시간대로 Ical.Net 전개 → 각 회차의 원래 키(`original_start_key`) 계산.
  2. 같은 키를 가진 예외 행이 있으면 그 원본 회차를 **제거**.
  3. 예외 행 중 `status <> 'cancelled'`이고 **실제 기간이 조회 범위와 겹치는 것**을 별도로 합친다 (11/1 회차를 12/1로 옮긴 예외도 12월 조회에 나타남).
- 조회 범위 앞쪽 여유: `rangeStart - (일정 길이 - 1)일`부터 전개 후 기간 겹침 필터.
- **생성 입력은 부분집합만** (MCP·UI): daily / weekly(요일 목록, 시작 요일 포함 필수) / monthly(시작일과 같은 '일') / yearly, `interval`, `until` 또는 `count`(동시 불가). Core가 RRULE로 변환. LLM이 RRULE 문자열을 직접 만들지 않는다.
- 부분집합 밖의 규칙(Google에서 온 것): 표시·회차 단위 완료/삭제는 지원, 규칙 편집은 불가("Google 캘린더에서 편집하세요").
- 회차 단위 작업 = 예외 행 생성 → Google 예외 인스턴스 patch:
  - 회차 완료: 예외 행 `is_done=1` → `deskcalDone`.
  - 회차 삭제: 예외 행 `status='cancelled'`.
- **예외가 하나라도 있는 시리즈의 규칙·시작일 변경은 거부**한다 ("Google 캘린더에서 편집하거나 회차 예외를 먼저 정리하세요"). Google이 시리즈 변경 시 예외를 어떻게 처리하는지 보장되지 않고, 로컬 예외를 먼저 지우면 pull로 다시 나타나거나 미전송 수정이 유실될 수 있기 때문. 예외가 없으면 허용.

### 4.5 소프트 삭제와 복구
- 삭제 = `deleted_at` + dirty → push 시 `events.delete`(`If-Match`) / `tasks.delete`.
- **영구 삭제(purge)는 "원격 삭제가 확인된 tombstone" 또는 "한 번도 push되지 않은 행"만**, 삭제 후 30일 경과 시. 30일 넘게 오프라인이어도 미전송 삭제를 잃지 않는다.
- 취소된 예외 회차(`status='cancelled'`)는 부모 시리즈가 있는 동안 유지한다(일반 30일 purge 대상 아님).
- 복구(`restore_*`): 로컬 `deleted_at` 해제 + dirty.
  - 아직 push 전이면 Google에는 아무 일도 없었던 것.
  - push 후면 원래 이벤트를 `status: confirmed`로 patch.
  - **404/410(원격에서 사라짐)일 때만** 재생성: 단일 일정은 같은 내용으로 새로 만들고, 시리즈는 새로 만들되 예외(회차 완료·삭제)는 복원되지 않는다고 사용자에게 알린다.
  - 재생성 시 새 세대의 Google ID(`google_gen` +1로 계산)를 **호출 전에 로컬에 저장**하고 재시도에는 같은 ID를 쓴다. 로컬 `event_uid`(MCP ID)는 바뀌지 않는다.
  - 403/429/timeout 등 다른 오류는 재생성으로 바꾸지 않고 다음 동기화에 재시도.
  - 할 일은 원격 복구 수단 대신 **재생성 정책**을 쓴다 (새 Google 작업으로 insert).

---

## 5. 동기화 (SyncEngine)

### 5.1 인증
- Google Cloud 프로젝트(사용자 소유)에서 Calendar·Tasks·Drive API 활성화, OAuth 클라이언트 유형 **데스크톱 앱**.
- 범위: `calendar.events`(기본 캘린더 읽기/쓰기 + 공휴일 캘린더 읽기 — `events.list` 공식 범위에 포함), `tasks`, `drive.appdata`.
- 흐름: 루프백(`http://127.0.0.1:<임의 포트>`) + PKCE — `GoogleWebAuthorizationBroker`의 **`usePkce: true` 오버로드**를 명시적으로 쓴다.
- 토큰 저장: 기본 `FileDataStore` 대신 **DPAPI(`ProtectedData`, CurrentUser)로 암호화하는 `IDataStore` 구현**, `%LOCALAPPDATA%\DeskCal\token.dat`.
- 로그인은 앱에서만 한다. PC마다 한 번.
- **계정 결합**: 첫 로그인한 Google 계정 ID를 `setting`에 저장. 다른 계정으로 로그인하면 거부한다(로컬 데이터 초기화 후에만 허용). 원래 계정의 dirty 행이 다른 계정에 올라가는 것을 막기 위함.
- **주의**: OAuth 동의 화면이 "테스트" 상태면 refresh token이 7일 후 만료된다. 개인용이라도 "프로덕션"으로 게시해야 한다(미검증 앱 경고는 뜨지만 본인 사용엔 문제없음). 설치 안내에 명시.

### 5.2 공통: push 규칙 (outbox)
1. dirty 행의 **snapshot**(내용 + `local_revision`)을 읽고 트랜잭션을 닫는다.
2. 트랜잭션 밖에서 HTTP 요청.
3. 응답 적용 트랜잭션에서 현재 `local_revision`을 다시 읽어:
   - 같으면 → 응답 내용·etag 반영, `is_dirty=0`, `push_state='synced'`.
   - 다르면(전송 중 로컬 수정) → Google ID·etag만 갱신하고 **dirty 유지** → 다음 회차에 최신 내용 전송.
4. 실패 → dirty 유지, `push_state='failed'`, `attempt_count`·`last_error` 기록. pull이 이 행을 덮지 않는다.

순서: 매 동기화는 **push → pull**. pull은 dirty·failed 행을 원격 값으로 덮지 않는다(충돌은 push 단계에서 412로 처리).

### 5.3 일정 (Calendar `primary`)
- **생성 중복 방지**: 로컬 생성 일정은 `google_event_id`를 insert 전에 `event_uid`(소문자 hex 32자, Calendar ID 허용 형식)로 정해 저장한다. 응답 유실 후 재시도에서 `409 Conflict`가 오면 그 ID를 `get`해서 `deskcalUid`/`deskcalGen`이 우리 것이고 `cancelled`가 아닌지 확인한 뒤 연결한다. 이는 "정확히 한 번" 보장이 아니라 **중복 재시도 방지**다.
- 최초: `events.list`(`singleEvents=false`, 모든 페이지) → 마지막 페이지의 `nextSyncToken` 저장. 예외가 부모보다 먼저 올 수 있으므로 `google_recurring_id`로 저장한 뒤 2단계로 `parent_event_id`를 해석.
- 증분: `events.list(syncToken)`. **모든 페이지를 적용한 뒤에만** 새 토큰을 저장한다(같은 쿼리 조건 유지).
- tombstone: 삭제 항목은 `id`(취소 예외는 `recurringEventId`·`originalStartTime`)만 보장되므로 **전용 분기**로 적용한다 — 기존 행의 값은 보존하고 `deleted_at`/`status`만 바꾸며, 로컬에 없는 삭제는 무시. 최소 정보뿐인 취소 예외는 원래 키에서 `start_date`/`end_date`를 채워 저장.
- `410 Gone`: 토큰 폐기 후 전체 재조회. **clean 행만** 원격과 전체 대조(원격에 없는 clean 행 제거), dirty·failed 행은 보존.
- 수정: `events.patch` + `If-Match: etag`. patch라서 DeskCal이 모르는 필드(장소, 참석자 등)는 보존된다.
- 예외 회차: 원본 시리즈의 `events.instances`에서 `originalStartTime`이 키와 맞는 인스턴스를 patch.
- 삭제: `events.delete` + `If-Match`.
- **충돌 (`412`) → 원격 우선** (결정적 정책): 로컬 변경을 폐기하고 원격 최신본을 반영, 폐기 내용을 `sync_conflict`에 남겨 트레이에 알린다. 원격 수정 vs 로컬 삭제도 원격 우선(로컬 삭제 취소). 시계 비교는 하지 않는다 — 다른 PC의 편집 시각과 업로드 시각이 달라 순서를 알 수 없기 때문. 같은 항목 재시도는 최대 3회.

### 5.4 할 일 (Tasks `@default`)
- 매 동기화마다 **기본 목록 전체 페이지 조회**(`showCompleted`, `showHidden`, `showDeleted` = true) + 전체 대조. 개인 목록은 작아서 가장 단순하고, `updatedMin` 워터마크의 누락 위험이 없다.
- push: insert / patch(`If-Match` — 412 동작은 M0.5에서 확인) / delete. 충돌 정책은 일정과 같음(원격 우선).
- **결과 불명 insert** (응답 유실·타임아웃): Tasks에는 생성 멱등성 수단이 없다. `push_state='unknown'`으로 두고 **자동 재전송·자동 연결을 하지 않는다**. 사이드바·트레이에 "전송 결과 확인 필요"를 표시하고, 같은 제목·마감일의 원격 작업은 후보로만 보여준다. 연결 또는 재전송은 사용자가 고른다.
- `due`는 `YYYY-MM-DDT00:00:00.000Z`로 보내고 날짜만 읽는다. 정렬은 `position` 순.

### 5.5 설정 (Drive `appDataFolder`)
- `settings.json` 하나: `{ "categories": { "1": "업무", "2": "개인", ... }, "weekStart": "SUN" }`.
- 파일은 **저장된 fileId로** 읽고 쓴다. 최초에 같은 이름 파일이 여럿이면 `createdTime` 오름차순, 동률이면 `fileId` 오름차순으로 첫 번째를 canonical로 택하고 나머지는 삭제한다(두 PC가 같은 결정을 내림).
- 문서 단위 `local_revision` + `is_dirty`(`sync_state`의 `settings` 행). 업로드 snapshot의 revision이 그대로일 때만 dirty 해제. 파일 전체 마지막 저장 우선.

### 5.6 공휴일
- 하루 1회 `events.list(공휴일 캘린더, timeMin=올해 1/1T00:00+09:00, timeMax=내후년 1/1T00:00+09:00)` — `timeMax`는 배타적. **모든 페이지 성공 후에만** `holiday` 캐시를 교체. 오프라인이면 캐시 사용.
- 캘린더 ID 후보 `ko.south_korea#holiday@group.v.calendar.google.com`와 휴일/기념일 구분(어버이날 등은 쉬는 날 아님) 규칙은 공식 보장이 없어 M0.5에서 실데이터로 확인한다.

---

## 6. 화면

### 6.1 디자인 원본
- Figma: https://www.figma.com/design/hWNjSHKq8230T4dTyVQ34V
  - `달력 · 배경 투명도 100% / 50%` — 사용자 작업 (월간 카드 기본)
  - `화면 · 월간 + 사이드바`, `화면 · 주간 + 사이드바`, `화면 · 일정 편집 + 검색`, `DeskCal · Components` — Claude 작업, 사용자 확정 (2026-10-30)
- 토큰 (WPF `ResourceDictionary`로 옮김):

  | 토큰 | 값 |
  |---|---|
  | 글꼴 | Noto Sans KR (Regular / Medium / Bold) |
  | 본문 / 보조 / 흐림 | `#25364A` / `#56677D` / `#B0BACB` |
  | 강조(오늘·선택) / 오늘 칸 배경 | `#246BDE` / `#EDF3FF` |
  | 일요일·공휴일 / 토요일 | `#C0413A` / `#3A6AC0` |
  | 카드 | 흰색 93%(투명도 100%) 또는 50% + 배경 흐림 20, 테두리 흰색 60%, 모서리 20, 그림자 `0 4 24 #263359@12%` |
  | 구분선 | `#344E6E` 4–8% |
  | 카테고리 | Google 이벤트 색 (`colors.get`) — 일정 칩 배경 14%, 왼쪽 선 100% |

### 6.2 관찰: 기존 앱
- 월간 날짜 칸 격자, 칸 = 머리줄(양력·음력·공휴일) + 자유 텍스트 본문.

### 6.3 레이아웃 (Figma 기준)
- 전체: 달력 카드 640 + 간격 12 + 사이드바 240 (§3.3).
- **헤더**: 연도(작게)·월(크게) | 월간/주간 토글 · 검색 버튼 · ◀ 오늘 ▶.
- **월간**: 요일 줄(일 빨강, 토 파랑) + **그 달에 필요한 주 수만큼(4–6줄)** 격자. 칸 = 날짜 숫자(공휴일은 빨간 숫자 + 이름) + 일정 칩 **최대 2개**, 넘치면 `+N`(클릭 시 그날 목록 팝업). 칩 = 카테고리 색, 시간 일정은 `15:00 치과`, 완료는 취소선·회색, 마감일이 그날인 할 일은 회색 칩. 이전/다음 달 날짜는 흐리게, 오늘은 파란 원 + 칸 배경. 여러 날 일정은 MVP에서 날짜마다 반복 표시.
- **주간**: 날짜 머리줄(오늘 강조) / 종일 줄(종일 일정 + 그날 마감 할 일) / 시간표(기본 스크롤 08시, 일정 블록 = 길이 비례·카테고리 색, 빨간 현재 시각 선).
- **하단 바**: "오늘 · 10월 30일 금요일" | 동기화 상태("Google 동기화됨 · 1분 전", 오류·확인 필요 시 경고 색) · `⋯` 메뉴(설정, 투명도 등).
- **배경 투명도**: 100%(불투명) / 50%(유리) 등 사용자 설정, PC별 저장.
- 토글 상태와 보던 날짜는 PC별 `setting`에 저장.

### 6.4 사이드바 계약 (`get_agenda`와 같은 Core 로직)
- 위: **D-day** 칩, 가까운 순.
  - 남은 일 = 회차 시작일 − 오늘. 0이면 "D-day"(강조), 지난 단일 D-day는 숨김.
  - 반복 D-day(매년 기념일 등)는 다음 회차 기준.
- 아래: **할 일 · 2주 일정** 통합 목록. 묶음 순서:
  1. **기한 지남** — 마감 지난 미완료 할 일
  2. **날짜별** — 오늘~13일 후: 그날 일정(반복 전개) + 그날 마감 할 일
  3. **이후** — 14일 이후 마감 미완료 할 일
  4. **언제든** — 마감 없는 미완료 할 일
  - 14일 제한은 일정에만 적용, 미완료 할 일은 전부 나온다.
- 체크: 할 일 → 완료, 일정 → `is_done`(반복이면 그 회차만).
  - 완료한 할 일은 `completed_at`이 오늘이면 취소선으로 남고 다음 날 사라짐.
  - 완료한 일정은 회차 날짜 기준으로 표시(날짜가 지나면 목록에서 빠짐).
- 하단 "할 일 추가" 입력란.
- 전송 결과 불명 할 일(§5.4)은 경고 표시.

### 6.5 편집·검색
- **클릭 규칙** (2026-10-30 확정): 날짜 칸·시간대·일정 칩 모두 **한 번 클릭은 아무 동작 없음**, **더블클릭해야** 추가·수정 팝업이 열린다(바탕화면 정리 중 오조작 방지, 기존 앱 사용 경험과 동일). 예외: 사이드바 **체크박스는 한 번 클릭**으로 완료 처리(명확한 목적의 작은 대상).
- **편집 팝업**: 빈 칸 더블클릭 → 추가, 항목 더블클릭 → 수정. 일정/할 일 전환, 제목, 날짜·종일, 시간, 반복, 카테고리(색 + 이름), D-day 스위치, 메모, 삭제/취소/저장. Claude가 넣은 항목은 "Claude가 추가함" 표시. 열린 동안 `Editing` 상태(§3.3).
- **검색**: 헤더 검색 버튼 또는 **Ctrl+F**. 제목·메모 부분 일치(대소문자 무시), 일정·할 일. 결과 = 날짜·시간 + 카테고리 선(할 일은 원), ↑↓·Enter로 해당 날짜 이동. 로컬 DB 검색(삭제·취소 제외).
- 카테고리 이름 관리: 설정 창.

---

## 7. MCP 서버

### 7.1 관찰: 기존 앱의 AI 연동
- 삭제·수정을 바로 실행하지 않고 확인을 한 번 더 받는 2단계 방식이다.

### 7.2 등록
```bash
claude mcp add deskcal -- "C:\Program Files\DeskCal\DeskCal.Mcp.exe"
```
Claude Desktop은 `claude_desktop_config.json`의 `mcpServers`에 같은 경로. 노트북·데스크톱 각각 등록. stdout은 MCP 전용, 로그는 stderr + `%LOCALAPPDATA%\DeskCal\logs\`.

### 7.3 공통 규칙
- 모든 응답에 `today`(`YYYY-MM-DD`)와 `weekday` 포함.
- 날짜 `YYYY-MM-DD`, 시각 `HH:MM` 로컬. 시간대 입력 없음.
- ID: `event_id`(= `event_uid`), `todo_id`(= `todo_uid`). 반복 회차는 불투명 **`occurrence_id`**(`<event_uid>~<original_start_key>`). Google ID는 노출하지 않는다.
- 부분 수정: 필드 **생략 = 유지**, **`null` = 해제**. 해제할 수 없는 필드(`title` 등)에 `null`이면 거부.
- 검증 실패는 `isError: true` + 사람이 읽을 메시지 → Claude가 고쳐서 재시도.
- 쓰기 결과의 동기화 상태(항목별): `synced`(그 revision이 원격에 반영됨) / `pending`(로컬에만, 나중에 자동 전송) / `conflict`(원격 우선으로 이 변경이 폐기됨) + `reason`(`offline`, `not_signed_in`, `busy` 등). 412로 폐기된 요청을 성공으로 보고하지 않는다.

### 7.4 도구 (14개)

**조회·분석 (6)**
| 도구 | 용도 | 입력 |
|---|---|---|
| `get_agenda` | 브리핑용 = 사이드바 데이터(§6.4) | `start_date?`, `end_date?` (생략 시 오늘~+13일) |
| `list_events` | 기간 일정(반복 전개) | `start_date?`, `end_date?` (생략 시 오늘~+30일, 최대 366일), `keyword?`, `category?`, `include_deleted?`, `cursor?` |
| `get_event` | 단건 | `event_id` 또는 `occurrence_id` |
| `find_free_time` | 빈 시간 찾기 | `start_date`, `end_date`(최대 31일), `duration_minutes`(>0), `day_start?`(기본 09:00), `day_end?`(기본 22:00) |
| `list_todos` | 할 일 | `status?`(open/done/all), `keyword?`, `include_deleted?` |
| `list_categories` | 카테고리 | — |

**일정 쓰기 (4)**
| 도구 | 입력 | annotations |
|---|---|---|
| `create_events` | `events`: 1~50개 | — |
| `update_event` | `event_id` 또는 `occurrence_id`, 바꿀 필드(+ `is_done`) | idempotent |
| `delete_event` | `event_id` 또는 `occurrence_id` | destructive |
| `restore_event` | `event_id` 또는 `occurrence_id` | — |

**할 일 쓰기 (4)**
| 도구 | 입력 | annotations |
|---|---|---|
| `create_todos` | `todos`: 1~50개 (`title`, `due_date?`, `memo?`) | — |
| `update_todo` | `todo_id`, `title?`, `due_date?`, `memo?`, `is_done?` | idempotent |
| `delete_todo` | `todo_id` | destructive |
| `restore_todo` | `todo_id` | — |

일정 필드: `title`(필수), `start_date`(필수), `end_date?`, `start_time?`, `end_time?`(생략 시 +1시간), `memo?`, `category?`(color_id 또는 이름), `is_dday?`, `repeat?`(`frequency`, `interval`, `weekdays`, `until`|`count`), `is_done?`(수정 시).

- `create_*`의 "전부 성공 또는 전부 실패"는 **로컬 트랜잭션에만** 해당한다. Google 전송은 항목별로 `synced`/`pending`을 보고한다.
- `update_event`에 `occurrence_id`면 그 회차만(예외 생성). 회차 단위로는 `is_done`과 삭제만 의미 있고, 내용 변경은 시리즈 전체로만. 예외가 있는 시리즈의 `repeat`/`start_date` 변경, Google에서 온 부분집합 밖 규칙의 `repeat` 변경은 거부(§4.4).
- `restore_event`: `event_id`면 시리즈 복구(개별 취소 회차는 유지), `occurrence_id`면 그 회차 취소만 해제.
- `list_events`는 200건마다 `cursor`로 페이지. 하루에 200건이 넘어도 빠짐없이 조회 가능.

도구 설명에 넣을 사용 지침:
- "상대 날짜를 계산하기 전에 오늘 날짜가 필요하면 먼저 `get_agenda`를 호출하라."
- "삭제 전에 조회로 ID를 확인하라. 제목이 같은 항목이 여럿이면 사용자에게 어느 것인지 물어라."
- "반복 일정 삭제·수정 시 이번 회차만인지 전체인지 사용자에게 확인하라."
- "브리핑·요약 요청에는 `get_agenda`를 쓰라."

`list_events` / `get_agenda`의 일정 항목:
```json
{ "event_id": "3f2a9c…", "occurrence_id": "3f2a9c…~2026-11-03T06:00:00Z",
  "title": "치과", "category": "개인",
  "start_date": "2026-11-03", "end_date": "2026-11-03", "start_time": "15:00", "end_time": "16:00",
  "is_done": false, "is_dday": false, "is_recurring": false, "repeat_editable": true,
  "created_by": "claude", "sync": "synced" }
```

`find_free_time`:
- 바쁨 = `is_busy=1`인 시간 일정. 종일 일정과 `transparent`(바쁘지 않음) 일정은 제외(설명에 명시).
- 절단 없는 내부 조회로 계산. 구간은 반열림 `[start, end)`.
- 날짜마다 `day_start`~`day_end` 안에서 `duration_minutes` 이상 빈 구간 목록 반환.

### 7.5 삭제 안전장치
- 2단계 확인 대신 **소프트 삭제 + 복구 도구** — 요구가 "지우라 하면 지워주는" 것이기 때문.
- `destructiveHint`는 클라이언트 승인 UI를 위한 **힌트일 뿐 강제가 아니다**.
- 서버가 강제하는 것은 ID 형식·존재 여부뿐이고, "조회 후 삭제"는 도구 설명의 지침이다.
- 보장되는 안전장치는 30일 소프트 삭제 + `restore_*`. Google 반영 후의 복구는 §4.5의 제약을 따른다.
- 일괄 삭제 도구는 없다.

---

## 8. 앱 생명주기
- 단일 인스턴스: named mutex `Local\DeskCal.App.SingleInstance`.
- 트레이: 위치 조정 잠금/해제, 지금 동기화, Google 계정, 설정, 시작 시 실행, 종료. 충돌(`sync_conflict`)·확인 필요 항목 알림.
- 시작 시 실행: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- `TaskbarCreated` 수신 시 트레이 재등록 + Z순서 재적용.

---

## 9. 테스트 전략
| 대상 | 방법 |
|---|---|
| 반복 전개 + 예외 | 원본 시간대 전개(DST 걸친 해외 시간대 반복), 키 매칭 제거, 이동 회차(다른 달로 옮긴 예외), 월말·윤년·격주·`UNTIL`/`COUNT`, 범위 이전 시작 여러 날 회차 |
| 생성 입력 → RRULE | 부분집합 각 경우, `count`+`until` 거부, 시작 요일 미포함 weekly 거부, 예외 있는 시리즈 구조 변경 거부 |
| Repository | CRUD, 소프트 삭제·복구(시리즈/회차), 부분 수정 생략/null, 검증 계약, 일괄 생성 로컬 원자성 |
| 시간대 | 원문↔파생 변환, 종일 `end.date` ±1일, 시각 미변경 patch에 start/end 미포함 |
| outbox·revision | 전송 응답 직전 로컬 재수정 → dirty 유지, 프로세스 종료 직후 재시작, pull 적용 중 로컬 쓰기 |
| SyncEngine (가짜 Google) | 최초(예외 먼저 도착), 증분, 페이지 중간 실패 시 토큰 미저장, 410 + dirty 보존, tombstone 최소 정보, 412 원격 우선 + `sync_conflict`, 응답 유실 후 409 연결, 복구 404→재생성 세대 ID, Tasks unknown 비자동 처리, settings canonical 수렴·revision |
| 동시성 | 겹치는 동시 쓰기, 동시 최초 실행 마이그레이션 1회, 잠금 타임아웃, 구버전 exe 거부, 동기화 mutex + MCP 10초 예산 |
| `get_agenda`·사이드바 | 묶음 순서, 14일 경계, 완료 표시 규칙, D-day 당일·지난·반복 |
| `find_free_time` | transparent 제외, 경계(일정이 day_start 걸침, 연속 일정), 반열림 |
| MCP 도구 | SDK in-memory 클라이언트로 호출 → 응답 스키마·오류 메시지·`sync` 상태 |
| 실제 Google | 테스트 계정 E2E (M0.5, M4) |
| 바탕화면 고정 | M-1 체크리스트 |

---

## 10. 마일스톤

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| M-1 | 바탕화면 고정 spike (빈 WPF 창 + Z순서 상태 머신 + 투명도) | §3.3 체크리스트를 노트북·데스크톱에서 통과. 실패 시 §3.2 재검토 |
| M0.5 | **Google 계약 spike** (테스트 계정, M0와 병행) | 예외·취소 인스턴스 형태, 삭제 이벤트 patch 복구, 시리즈 변경 시 예외 처리, Tasks `If-Match`/412, 공휴일 캘린더 ID·휴일 구분, DST 반복, `colors.get` 확인 → **스키마 확정** |
| M0 | 솔루션, Core 스키마·마이그레이션, Repository, 반복 전개(Ical.Net + 예외) | Core·동시성 테스트 통과 |
| M1 | MCP 14개 도구 (로컬 전용) | Claude Code에서: "내일 3시 치과" 생성(15–16시), "이번 주 정리해 줘" 브리핑, "다음 주 2시간 비는 날", "이 3개 할 일로 넣어줘" 일괄, 삭제→복구 |
| M2 | WPF 월간/주간 + 사이드바 + D-day + 최하단 고정 + 변경 알림 (Figma 디자인 적용) | Claude로 추가한 일정이 신호 정상 1초·유실 30초 내 표시. 사이드바 체크 동작. Figma와 시각 일치 |
| M3 | 앱 내 편집, 카테고리, 검색(Ctrl+F), 트레이, 자동 시작, 위치 조정 | UI만으로 일정·할 일 전 기능 사용 가능 |
| M4 | Google 동기화 (OAuth, Calendar, Tasks, appData, 공휴일) | 노트북에서 Claude로 넣은 일정·할 일이 **앱이 실행 중인 데스크톱에 3분 이내** 표시(2분 폴링 + 전송·네트워크 여유). 휴대폰 Google 캘린더 일정 표시. 응답 유실 재시도, 전송 중 편집, 410 + dirty, 취소 예외, DST, 이동 회차, 시리즈/회차 복구, 오프라인 후 재연결 전송 |

- M1을 UI보다 먼저: Claude 연동이 핵심이고 UI 없이 바로 검증 가능.
- M0.5를 M0와 병행: Google 데이터 형태가 스키마를 좌우하므로, 본 통합(M4) 전에 계약만 먼저 확인해 나중의 마이그레이션을 줄인다.
- M-1은 경험적 동작에 기대는 부분이라 맨 앞에서 위험을 걷어낸다.

---

## 11. 열린 질문 / 확인 필요
1. M0.5에서 확인: 삭제 이벤트 patch 복구, 시리즈 변경 시 예외 처리, Tasks `If-Match`/412, 공휴일 캘린더 ID·휴일 구분, `colors.get` 팔레트.
2. 배포: self-contained 단일 exe 권장 (두 PC에 .NET 런타임 설치 불필요).

---

## 부록: 결정 이력
- v2: Codex(GPT-6-Astra, low) 검토 10건 수용 — Z순서 상태 머신·M-1 spike, `data_version` 감시 연결, 마이그레이션 직렬화·동시성 테스트, annotation 비강제성, 회차 복구·삭제 ID 재조회, 부분 수정 계약, RRULE 제약, 기간 겹침 조회, 상대 날짜 기준일·기본 종료 시각, 음력 범위·윤달 매핑.
- v3: 사용자 요구 확정으로 재설계 — 할 일·D-day·주간 보기·사이드바·검색·카테고리·Claude 확장 도구·Google 전체 동기화(두 PC). 음력 계산 제거(Google 휴일 캘린더), Ical.Net 도입, 회차 예외를 Google 모델로.
- v3.1: Codex v3 검토 15건 + 후속 3건 수용 — outbox revision(1), Calendar 결정적 ID·409 확인(2, B), dirty·410·purge 보호와 2단계 부모 해석(3), 원문 시간대 보존(4), 회차 키·이동 예외·`occurrence_id`(5), tentative·tombstone 분기(6), 예외 있는 시리즈 변경 거부·복구 재생성 제한(7), 412 원격 우선 + `sync_conflict`(8), Tasks 전체 대조(9), settings fileId·canonical·revision(10, C), 도구 14개·로컬 원자성·항목별 sync 상태(11), 사이드바·D-day 계약(12), transparency·cursor(13), PKCE·DPAPI·계정 결합(14), M0.5 spike·M4 기준(15), Tasks unknown 비자동 처리(A). 디자인 확정: 폭 892(아이콘 회피), 월간 줄 수 가변, 칩 최대 2개 + `+N`, 투명도 설정, 동기화 상태 표시, Ctrl+F.
