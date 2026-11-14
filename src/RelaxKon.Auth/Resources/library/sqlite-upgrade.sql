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
