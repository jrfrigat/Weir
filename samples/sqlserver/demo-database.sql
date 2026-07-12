/* ============================================================================
   Weir demo database for SQL Server.

   Creates the WeirDemo database with a small "sales" domain plus stored
   procedures, functions and a table type that exercise every Weir feature:

     - multi-row and single-row result sets
     - a scalar function and a table-valued function
     - output and input-output parameters
     - a procedure RETURN value
     - a table-valued parameter (TVP)
     - multiple result sets in one call
     - a call with no result set (values only through output parameters)
     - informational (PRINT / low-severity RAISERROR) messages
     - error handling with THROW (mapped by Weir to problem+json)

   Safe to re-run: objects use CREATE OR ALTER / existence checks, and seed
   data is inserted only when the tables are empty. To start from scratch,
   drop the database first:  DROP DATABASE IF EXISTS WeirDemo;

   Run with sqlcmd:  sqlcmd -S <server> -E -i demo-database.sql
   or open it in SQL Server Management Studio and execute.
   ============================================================================ */

IF DB_ID('WeirDemo') IS NULL
    CREATE DATABASE WeirDemo;
GO

USE WeirDemo;
GO

/* ---- schema ------------------------------------------------------------- */
IF SCHEMA_ID('sales') IS NULL
    EXEC('CREATE SCHEMA sales');
GO

/* ---- tables ------------------------------------------------------------- */
IF OBJECT_ID('sales.Customers', 'U') IS NULL
    CREATE TABLE sales.Customers
    (
        CustomerId INT           IDENTITY(1, 1) PRIMARY KEY,
        Name       NVARCHAR(100) NOT NULL,
        Email      NVARCHAR(200) NULL,
        CreatedAt  DATETIME2     NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT SYSUTCDATETIME()
    );

IF OBJECT_ID('sales.Products', 'U') IS NULL
    CREATE TABLE sales.Products
    (
        ProductId INT            IDENTITY(1, 1) PRIMARY KEY,
        Name      NVARCHAR(100)  NOT NULL,
        Price     DECIMAL(10, 2) NOT NULL,
        Stock     INT            NOT NULL CONSTRAINT DF_Products_Stock DEFAULT 0
    );

IF OBJECT_ID('sales.Orders', 'U') IS NULL
    CREATE TABLE sales.Orders
    (
        OrderId    INT            IDENTITY(1, 1) PRIMARY KEY,
        CustomerId INT            NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES sales.Customers (CustomerId),
        CreatedAt  DATETIME2      NOT NULL CONSTRAINT DF_Orders_CreatedAt DEFAULT SYSUTCDATETIME(),
        Total      DECIMAL(12, 2) NOT NULL CONSTRAINT DF_Orders_Total DEFAULT 0
    );

IF OBJECT_ID('sales.OrderItems', 'U') IS NULL
    CREATE TABLE sales.OrderItems
    (
        OrderItemId INT            IDENTITY(1, 1) PRIMARY KEY,
        OrderId     INT            NOT NULL CONSTRAINT FK_OrderItems_Orders REFERENCES sales.Orders (OrderId),
        ProductId   INT            NOT NULL CONSTRAINT FK_OrderItems_Products REFERENCES sales.Products (ProductId),
        Quantity    INT            NOT NULL,
        UnitPrice   DECIMAL(10, 2) NOT NULL
    );
GO

/* ---- table-valued parameter type ---------------------------------------- */
IF TYPE_ID('sales.OrderItemType') IS NULL
    CREATE TYPE sales.OrderItemType AS TABLE
    (
        ProductId INT NOT NULL,
        Quantity  INT NOT NULL
    );
GO

/* ---- seed data ---------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sales.Customers)
    INSERT INTO sales.Customers (Name, Email) VALUES
        (N'Acme Corp', N'orders@acme.example'),
        (N'Globex',    N'buy@globex.example'),
        (N'Initech',   N'ap@initech.example');

IF NOT EXISTS (SELECT 1 FROM sales.Products)
    INSERT INTO sales.Products (Name, Price, Stock) VALUES
        (N'Widget',   9.99,  100),
        (N'Gadget',  19.50,   40),
        (N'Gizmo',   49.00,   15),
        (N'Sprocket', 2.25,  500);
GO

/* ---- procedures --------------------------------------------------------- */

-- Multi-row: every product.
CREATE OR ALTER PROCEDURE sales.GetProducts
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProductId, Name, Price, Stock FROM sales.Products ORDER BY ProductId;
END;
GO

-- Single-row: one product by id (zero or one row).
CREATE OR ALTER PROCEDURE sales.GetProductById
    @ProductId INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProductId, Name, Price, Stock FROM sales.Products WHERE ProductId = @ProductId;
END;
GO

-- Filtered multi-row: optional query parameters.
CREATE OR ALTER PROCEDURE sales.SearchProducts
    @NamePattern NVARCHAR(100) = NULL,
    @MaxPrice    DECIMAL(10, 2) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SELECT ProductId, Name, Price, Stock
    FROM sales.Products
    WHERE (@NamePattern IS NULL OR Name LIKE '%' + @NamePattern + '%')
      AND (@MaxPrice IS NULL OR Price <= @MaxPrice)
    ORDER BY Price;
END;
GO

-- Table-valued parameter + output parameters + RETURN value: create an order.
-- @OrderId and @Total flow back as output parameters; the item count is the RETURN value.
CREATE OR ALTER PROCEDURE sales.CreateOrder
    @CustomerId INT,
    @Items      sales.OrderItemType READONLY,
    @OrderId    INT OUTPUT,
    @Total      DECIMAL(12, 2) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF NOT EXISTS (SELECT 1 FROM sales.Customers WHERE CustomerId = @CustomerId)
        THROW 50001, 'Unknown customer.', 1;

    IF NOT EXISTS (SELECT 1 FROM @Items)
        THROW 50002, 'An order must contain at least one item.', 1;

    IF EXISTS (SELECT 1 FROM @Items i WHERE NOT EXISTS (SELECT 1 FROM sales.Products p WHERE p.ProductId = i.ProductId))
        THROW 50003, 'One or more items reference an unknown product.', 1;

    DECLARE @count INT = (SELECT COUNT(*) FROM @Items);

    BEGIN TRAN;
        INSERT INTO sales.Orders (CustomerId) VALUES (@CustomerId);
        SET @OrderId = SCOPE_IDENTITY();

        INSERT INTO sales.OrderItems (OrderId, ProductId, Quantity, UnitPrice)
        SELECT @OrderId, i.ProductId, i.Quantity, p.Price
        FROM @Items i JOIN sales.Products p ON p.ProductId = i.ProductId;

        SELECT @Total = SUM(Quantity * UnitPrice) FROM sales.OrderItems WHERE OrderId = @OrderId;
        UPDATE sales.Orders SET Total = @Total WHERE OrderId = @OrderId;
    COMMIT;

    PRINT CONCAT('Created order ', @OrderId, ' with total ', @Total);
    RETURN @count;
END;
GO

-- Multiple result sets: the order header, then its line items.
CREATE OR ALTER PROCEDURE sales.GetOrderWithItems
    @OrderId INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT OrderId, CustomerId, CreatedAt, Total FROM sales.Orders WHERE OrderId = @OrderId;

    SELECT oi.OrderItemId, oi.ProductId, p.Name, oi.Quantity, oi.UnitPrice
    FROM sales.OrderItems oi
    JOIN sales.Products p ON p.ProductId = oi.ProductId
    WHERE oi.OrderId = @OrderId
    ORDER BY oi.OrderItemId;
END;
GO

-- No result set: customer statistics returned only through output parameters.
CREATE OR ALTER PROCEDURE sales.GetCustomerStats
    @CustomerId INT,
    @OrderCount INT OUTPUT,
    @TotalSpent DECIMAL(12, 2) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @OrderCount = COUNT(*), @TotalSpent = ISNULL(SUM(Total), 0)
    FROM sales.Orders WHERE CustomerId = @CustomerId;
END;
GO

-- Input-output parameter: adds the product's stock to a running total that is passed in and out.
CREATE OR ALTER PROCEDURE sales.AccumulateStock
    @ProductId INT,
    @Running   INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @Running = ISNULL(@Running, 0)
                 + ISNULL((SELECT Stock FROM sales.Products WHERE ProductId = @ProductId), 0);
END;
GO

-- Writes plus validation and an output parameter; THROWs on a bad request.
CREATE OR ALTER PROCEDURE sales.AdjustInventory
    @ProductId INT,
    @Delta     INT,
    @Stock     INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @current INT = (SELECT Stock FROM sales.Products WHERE ProductId = @ProductId);
    IF @current IS NULL
        THROW 50010, 'Unknown product.', 1;

    IF @current + @Delta < 0
        THROW 50011, 'Adjustment would drive stock below zero.', 1;

    UPDATE sales.Products SET Stock = Stock + @Delta WHERE ProductId = @ProductId;
    SET @Stock = @current + @Delta;

    PRINT CONCAT('Stock for product ', @ProductId, ' adjusted by ', @Delta, ' to ', @Stock);
END;
GO

-- Informational messages: PRINT and a severity-0 RAISERROR are captured in the response "messages".
CREATE OR ALTER PROCEDURE sales.Ping
AS
BEGIN
    SET NOCOUNT ON;
    PRINT 'pong';
    RAISERROR('Weir demo is alive (from %s).', 0, 1, 'sales.Ping') WITH NOWAIT;
    SELECT CAST(1 AS BIT) AS Ok, SYSUTCDATETIME() AS ServerTimeUtc;
END;
GO

/* ---- functions ---------------------------------------------------------- */

-- Scalar function: the current price of a product.
CREATE OR ALTER FUNCTION sales.GetProductPrice (@ProductId INT)
RETURNS DECIMAL(10, 2)
AS
BEGIN
    RETURN (SELECT Price FROM sales.Products WHERE ProductId = @ProductId);
END;
GO

-- Inline table-valued function: all orders for a customer.
CREATE OR ALTER FUNCTION sales.GetOrdersByCustomer (@CustomerId INT)
RETURNS TABLE
AS
RETURN
(
    SELECT OrderId, CreatedAt, Total FROM sales.Orders WHERE CustomerId = @CustomerId
);
GO

/* ---- summary ------------------------------------------------------------ */
PRINT 'WeirDemo ready. Objects in schema [sales]:';
SELECT s.name AS [schema], o.name AS [object], o.type_desc AS [type]
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = 'sales' AND o.type IN ('U', 'P', 'FN', 'IF', 'TF')
ORDER BY o.type_desc, o.name;
GO
