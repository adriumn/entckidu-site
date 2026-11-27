-- 001: the Albums table (one row per album, the work itself – not your copies)
-- Run once, in the Azure portal: database entckiduSqlDb -> Query editor.

CREATE TABLE dbo.Albums (
    Id      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Albums PRIMARY KEY,
    Artist  NVARCHAR(200)     NOT NULL,
    Album   NVARCHAR(300)     NOT NULL,
    CONSTRAINT UQ_Albums_Artist_Album UNIQUE (Artist, Album)
);
