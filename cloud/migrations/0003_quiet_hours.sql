ALTER TABLE shouts ADD COLUMN quiet_override INTEGER NOT NULL DEFAULT 0 CHECK (quiet_override IN (0, 1));

CREATE TABLE quiet_periods (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  weekdays TEXT NOT NULL,
  start_minute INTEGER NOT NULL CHECK (start_minute >= 0 AND start_minute < 1440),
  end_minute INTEGER NOT NULL CHECK (end_minute > 0 AND end_minute <= 1440),
  sort_order INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
