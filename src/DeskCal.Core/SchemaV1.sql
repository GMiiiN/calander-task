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
