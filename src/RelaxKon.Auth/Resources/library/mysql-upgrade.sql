-- Additive maintenance migration. Back up and stop the server before applying.
CREATE TABLE LibraryFiles (
    Id varchar(32) NOT NULL PRIMARY KEY,
    AccountId int NOT NULL,
    Name varchar(200) NOT NULL,
    ContentType varchar(100) NOT NULL,
    Size bigint NOT NULL,
    Content longblob NOT NULL,
    CreatedAt datetime(6) NOT NULL,
    UpdatedAt datetime(6) NOT NULL,
    Version bigint NOT NULL,
    INDEX IX_LibraryFiles_AccountId_UpdatedAt (AccountId, UpdatedAt),
    CONSTRAINT FK_LibraryFiles_Accounts_AccountId FOREIGN KEY (AccountId) REFERENCES Accounts(AccountId) ON DELETE CASCADE
);
