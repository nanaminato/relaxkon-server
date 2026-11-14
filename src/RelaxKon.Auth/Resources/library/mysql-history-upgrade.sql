CREATE TABLE IF NOT EXISTS LibraryRevisions (
    FileId varchar(32) NOT NULL,
    Version bigint NOT NULL,
    Name varchar(200) NOT NULL,
    Content longblob NOT NULL,
    UpdatedAt datetime(6) NOT NULL,
    PRIMARY KEY (FileId, Version),
    CONSTRAINT FK_LibraryRevisions_LibraryFiles FOREIGN KEY (FileId) REFERENCES LibraryFiles(Id) ON DELETE CASCADE
);
