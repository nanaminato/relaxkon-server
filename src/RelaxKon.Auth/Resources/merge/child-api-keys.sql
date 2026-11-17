CREATE TABLE IF NOT EXISTS ChildApiKeys (
    Id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    AccountId INT NOT NULL,
    SupplierKeyId INT NOT NULL,
    Name VARCHAR(100) NOT NULL,
    Prefix VARCHAR(11) NOT NULL,
    SecretHash VARCHAR(64) NOT NULL,
    CreatedAt BIGINT NOT NULL,
    ExpiresAt BIGINT NOT NULL,
    RevokedAt BIGINT NULL,
    UNIQUE KEY IX_ChildApiKeys_SecretHash (SecretHash),
    KEY IX_ChildApiKeys_AccountId_Id (AccountId, Id),
    KEY IX_ChildApiKeys_SupplierKeyId (SupplierKeyId),
    FOREIGN KEY (AccountId) REFERENCES Accounts (AccountId) ON DELETE CASCADE,
    FOREIGN KEY (SupplierKeyId) REFERENCES SupplierKeys (SupplierKeyId) ON DELETE CASCADE
) ENGINE=InnoDB;
