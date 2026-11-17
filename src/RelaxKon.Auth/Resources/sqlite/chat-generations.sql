CREATE TABLE IF NOT EXISTS ChatGenerations (
    AccountId INTEGER NOT NULL REFERENCES Accounts (AccountId) ON DELETE CASCADE,
    Id TEXT NOT NULL,
    State TEXT NOT NULL,
    RequestJson TEXT,
    SourceFingerprint TEXT,
    ErrorCode TEXT,
    CreatedAt INTEGER NOT NULL,
    LeaseUntil INTEGER NOT NULL,
    HistoryId INTEGER,
    MessageId INTEGER,
    Model TEXT,
    FrameCount INTEGER NOT NULL,
    OutputBytes INTEGER NOT NULL,
    PRIMARY KEY (AccountId, Id)
);
CREATE INDEX IF NOT EXISTS IX_ChatGenerations_State_LeaseUntil ON ChatGenerations (State, LeaseUntil);
CREATE INDEX IF NOT EXISTS IX_ChatGenerations_AccountId_HistoryId_CreatedAt ON ChatGenerations (AccountId, HistoryId, CreatedAt);

CREATE TABLE IF NOT EXISTS ChatGenerationFrames (
    AccountId INTEGER NOT NULL,
    JobId TEXT NOT NULL,
    Sequence INTEGER NOT NULL,
    Payload TEXT NOT NULL,
    PRIMARY KEY (AccountId, JobId, Sequence),
    FOREIGN KEY (AccountId, JobId) REFERENCES ChatGenerations (AccountId, Id) ON DELETE CASCADE
);
