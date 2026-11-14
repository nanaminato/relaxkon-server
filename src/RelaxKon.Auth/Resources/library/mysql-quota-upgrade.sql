CREATE TABLE IF NOT EXISTS LibraryQuota (
    Id int NOT NULL PRIMARY KEY,
    MaxBytes bigint NOT NULL,
    MaxFileBytes bigint NOT NULL
);
