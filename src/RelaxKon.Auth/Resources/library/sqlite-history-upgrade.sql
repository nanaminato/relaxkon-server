CREATE TABLE IF NOT EXISTS LibraryRevisions (
    FileId TEXT NOT NULL,
    Version INTEGER NOT NULL,
    Name TEXT NOT NULL,
    Content BLOB NOT NULL,
    UpdatedAt TEXT NOT NULL,
    PRIMARY KEY (FileId, Version),
    CONSTRAINT FK_LibraryRevisions_LibraryFiles FOREIGN KEY (FileId) REFERENCES LibraryFiles(Id) ON DELETE CASCADE
);
