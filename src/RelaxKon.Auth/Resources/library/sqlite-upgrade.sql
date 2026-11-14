-- Additive maintenance migration. Back up and stop the server before applying.
CREATE TABLE LibraryFiles (
    Id TEXT NOT NULL PRIMARY KEY,
    AccountId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    ContentType TEXT NOT NULL,
    Size INTEGER NOT NULL,
    Content BLOB NOT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL,
    Version INTEGER NOT NULL,
    CONSTRAINT FK_LibraryFiles_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts(AccountId) ON DELETE CASCADE
);
CREATE INDEX IX_LibraryFiles_AccountId_UpdatedAt ON LibraryFiles(AccountId, UpdatedAt);

CREATE TABLE IF NOT EXISTS LibraryQuota (
    Id INTEGER NOT NULL PRIMARY KEY,
    MaxBytes INTEGER NOT NULL,
    MaxFileBytes INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS LibraryRevisions (
    FileId TEXT NOT NULL,
    Version INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Content BLOB NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY (FileId, Version),
    CONSTRAINT FK_LibraryRevisions_LibraryFiles FOREIGN KEY (FileId) REFERENCES LibraryFiles(Id) ON DELETE CASCADE
);
