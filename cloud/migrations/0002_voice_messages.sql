-- Voice metadata is kept with the existing shout history. Audio files are temporary
-- server-side objects and are removed after the classroom acknowledges playback.
ALTER TABLE shouts ADD COLUMN content_type TEXT NOT NULL DEFAULT 'text';
ALTER TABLE shouts ADD COLUMN audio_path TEXT;
ALTER TABLE shouts ADD COLUMN audio_size INTEGER;
ALTER TABLE shouts ADD COLUMN audio_duration_ms INTEGER;
