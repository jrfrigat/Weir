-- Sample SQL Server schema for trying Weir end to end.
-- Run against a scratch database, then point a Weir data connection named "default" at it and
-- import samples/endpoints.seed.json from the admin UI (Endpoints -> Import).

IF OBJECT_ID('dbo.Widgets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Widgets
    (
        Id        INT            IDENTITY(1, 1) PRIMARY KEY,
        Name      NVARCHAR(100)  NOT NULL,
        Price     DECIMAL(10, 2) NOT NULL,
        CreatedAt DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
GO

-- Returns every widget as a single result set.
CREATE OR ALTER PROCEDURE dbo.GetWidgets
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Name, Price, CreatedAt FROM dbo.Widgets ORDER BY Id;
END;
GO

-- Returns one widget by id (zero or one row).
CREATE OR ALTER PROCEDURE dbo.GetWidgetById
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, Name, Price, CreatedAt FROM dbo.Widgets WHERE Id = @Id;
END;
GO

-- Inserts a widget, returns the new id through an output parameter and the row count as the
-- procedure return value. Demonstrates output parameters and return values.
CREATE OR ALTER PROCEDURE dbo.CreateWidget
    @Name  NVARCHAR(100),
    @Price DECIMAL(10, 2),
    @NewId INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Widgets (Name, Price) VALUES (@Name, @Price);
    SET @NewId = SCOPE_IDENTITY();
    RETURN @@ROWCOUNT;
END;
GO

-- A table-valued parameter type and a procedure that bulk-inserts through it.
IF TYPE_ID('dbo.WidgetImportType') IS NULL
BEGIN
    CREATE TYPE dbo.WidgetImportType AS TABLE
    (
        Name  NVARCHAR(100)  NOT NULL,
        Price DECIMAL(10, 2) NOT NULL
    );
END;
GO

-- Bulk-inserts widgets from a table-valued parameter and returns how many rows were inserted.
CREATE OR ALTER PROCEDURE dbo.ImportWidgets
    @Items dbo.WidgetImportType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.Widgets (Name, Price) SELECT Name, Price FROM @Items;
    SELECT @@ROWCOUNT AS Imported;
END;
GO
