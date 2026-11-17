CREATE TABLE IF NOT EXISTS ChatGenerations (
    AccountId INT NOT NULL,
    Id VARCHAR(32) NOT NULL,
    State VARCHAR(16) NOT NULL,
    RequestJson LONGTEXT NULL,
    SourceFingerprint VARCHAR(64) NULL,
    ErrorCode VARCHAR(80) NULL,
    CreatedAt BIGINT NOT NULL,
    LeaseUntil BIGINT NOT NULL,
    HistoryId BIGINT NULL,
    MessageId BIGINT NULL,
    Model VARCHAR(200) NULL,
    FrameCount INT NOT NULL,
    OutputBytes INT NOT NULL,
    PRIMARY KEY (AccountId, Id),
    KEY IX_ChatGenerations_State_LeaseUntil (State, LeaseUntil),
    KEY IX_ChatGenerations_AccountId_HistoryId_CreatedAt (AccountId, HistoryId, CreatedAt),
    FOREIGN KEY (AccountId) REFERENCES Accounts (AccountId) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS ChatGenerationFrames (
    AccountId INT NOT NULL,
    JobId VARCHAR(32) NOT NULL,
    Sequence INT NOT NULL,
    Payload LONGTEXT NOT NULL,
    PRIMARY KEY (AccountId, JobId, Sequence),
    FOREIGN KEY (AccountId, JobId) REFERENCES ChatGenerations (AccountId, Id) ON DELETE CASCADE
) ENGINE=InnoDB;
