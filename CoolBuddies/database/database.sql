-- CoolBuddies database (SQL Server). Run as an admin, then change the password below.
CREATE DATABASE CoolBuddies;
GO
USE CoolBuddies;
GO
CREATE TABLE Products (
  Id INT IDENTITY PRIMARY KEY,
  Name NVARCHAR(120) NOT NULL,
  Description NVARCHAR(250) NULL,
  Price DECIMAL(10,2) NOT NULL CHECK (Price > 0),
  Active BIT NOT NULL DEFAULT 1);

CREATE TABLE Orders (
  Id INT IDENTITY PRIMARY KEY,
  Reference UNIQUEIDENTIFIER NOT NULL UNIQUE,          -- public id sent to PayFast, so order numbers cannot be guessed
  FirstName NVARCHAR(60) NOT NULL, LastName NVARCHAR(60) NOT NULL,
  Email NVARCHAR(120) NOT NULL, Phone NVARCHAR(20) NOT NULL,
  Address NVARCHAR(250) NULL, Notes NVARCHAR(500) NULL,
  Total DECIMAL(10,2) NOT NULL,
  Status VARCHAR(10) NOT NULL DEFAULT 'Pending' CHECK (Status IN ('Pending','Paid','Failed')),
  CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
  PaidAt DATETIME2 NULL);

CREATE TABLE OrderItems (
  OrderId INT NOT NULL REFERENCES Orders(Id),
  ProductId INT NOT NULL REFERENCES Products(Id),
  Qty INT NOT NULL CHECK (Qty BETWEEN 1 AND 10),
  UnitPrice DECIMAL(10,2) NOT NULL,                    -- price at time of purchase
  PRIMARY KEY (OrderId, ProductId));

CREATE TABLE Payments (
  Id INT IDENTITY PRIMARY KEY,
  OrderId INT NOT NULL REFERENCES Orders(Id),
  GatewayPaymentId VARCHAR(50) NOT NULL UNIQUE,        -- stops a repeated PayFast notification being recorded twice
  Amount DECIMAL(10,2) NOT NULL,
  CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());

INSERT INTO Products (Name, Description, Price) VALUES
 ('12000btu Midwall Split Unit', 'Supply and installation', 7000.00),
 ('18000btu Midwall Split Unit', 'Supply and installation', 11400.00),
 ('Car Aircon Regas', 'Car air conditioner gas refill', 450.00);
GO
-- The API connects with this limited login, never as an admin.
CREATE LOGIN coolbuddies_api WITH PASSWORD = 'CHANGE-ME-to-a-long-random-password-1!';
CREATE USER coolbuddies_api FOR LOGIN coolbuddies_api;
GRANT SELECT ON Products TO coolbuddies_api;
GRANT SELECT, INSERT, UPDATE ON Orders TO coolbuddies_api;
GRANT INSERT ON OrderItems TO coolbuddies_api;
GRANT SELECT, INSERT ON Payments TO coolbuddies_api;
