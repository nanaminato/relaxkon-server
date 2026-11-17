CREATE TABLE IF NOT EXISTS ChildApiKeys (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    AccountId INTEGER NOT NULL REFERENCES Accounts (AccountId) ON DELETE CASCADE,
    SupplierKeyId INTEGER NOT NULL REFERENCES SupplierKeys (SupplierKeyId) ON DELETE CASCADE,
    Name TEXT NOT NULL,
    Prefix TEXT NOT NULL,
    SecretHash TEXT NOT NULL,
    CreatedAt INTEGER NOT NULL,
    ExpiresAt INTEGER NOT NULL,
    RevokedAt INTEGER
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_ChildApiKeys_SecretHash ON ChildApiKeys (SecretHash);
CREATE INDEX IF NOT EXISTS IX_ChildApiKeys_AccountId_Id ON ChildApiKeys (AccountId, Id);
CREATE INDEX IF NOT EXISTS IX_ChildApiKeys_SupplierKeyId ON ChildApiKeys (SupplierKeyId);
