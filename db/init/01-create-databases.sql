-- shop133 — una base de datos por servicio (Fase 0.4)
--
-- Lo ejecuta el servicio db-init de docker-compose.yml, con sqlcmd y como sa,
-- en cuanto el healthcheck de sqlserver pasa a healthy.
--
-- Este script es IDEMPOTENTE: corre entero en cada "docker compose up". Toda
-- creacion va detras de una guarda; ALTER ROLE ... ADD MEMBER ya lo es de por si.
--
-- Que hace, por cada servicio:
--
--   1. CREATE DATABASE <Servicio>Db
--   2. CREATE LOGIN <servicio>_user           (login de servidor)
--   3. CREATE USER  <servicio>_user           (usuario DENTRO de su base)
--   4. ALTER ROLE db_owner ADD MEMBER ...     (permisos solo ahi)
--
-- El punto 3 es el que importa: un login SIN usuario en una base ajena no
-- puede ni conectarse a ella (Msg 4060 / Msg 916). Asi la regla 1 de CLAUDE.md
-- ("una base por servicio, nadie toca la del vecino") deja de ser una
-- convencion y pasa a estar aplicada por el motor.
--
-- $(VAR) es sustitucion de variables de sqlcmd, no de Compose: sqlcmd las lee
-- del entorno del contenedor db-init. Compose no toca "$(...)", solo "${...}",
-- asi que este archivo no necesita escapes.
--
-- db_owner y no db_datareader/db_datawriter: EF Core necesita crear tablas y
-- escribir __EFMigrationsHistory desde la Fase 1.2.

SET NOCOUNT ON;
GO

-- ============================================================
-- Catalog
-- ============================================================
IF DB_ID('CatalogDb') IS NULL
    CREATE DATABASE CatalogDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'catalog_user')
    CREATE LOGIN catalog_user
        WITH PASSWORD = '$(CATALOG_DB_PASSWORD)',
             DEFAULT_DATABASE = CatalogDb,
             CHECK_POLICY = ON;
GO

-- USE necesita su propio batch: el contexto de base se resuelve al compilar,
-- no al ejecutar. Sin el GO, el CREATE USER de abajo iria contra master.
USE CatalogDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'catalog_user')
    CREATE USER catalog_user FOR LOGIN catalog_user;
ALTER ROLE db_owner ADD MEMBER catalog_user;
GO

-- ============================================================
-- Orders  (incluye el estado de la Saga, Fase 4.5)
-- ============================================================
USE master;
GO

IF DB_ID('OrdersDb') IS NULL
    CREATE DATABASE OrdersDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'orders_user')
    CREATE LOGIN orders_user
        WITH PASSWORD = '$(ORDERS_DB_PASSWORD)',
             DEFAULT_DATABASE = OrdersDb,
             CHECK_POLICY = ON;
GO

USE OrdersDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'orders_user')
    CREATE USER orders_user FOR LOGIN orders_user;
ALTER ROLE db_owner ADD MEMBER orders_user;
GO

-- ============================================================
-- Inventory
-- ============================================================
USE master;
GO

IF DB_ID('InventoryDb') IS NULL
    CREATE DATABASE InventoryDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'inventory_user')
    CREATE LOGIN inventory_user
        WITH PASSWORD = '$(INVENTORY_DB_PASSWORD)',
             DEFAULT_DATABASE = InventoryDb,
             CHECK_POLICY = ON;
GO

USE InventoryDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'inventory_user')
    CREATE USER inventory_user FOR LOGIN inventory_user;
ALTER ROLE db_owner ADD MEMBER inventory_user;
GO

-- ============================================================
-- Payments
-- ============================================================
USE master;
GO

IF DB_ID('PaymentsDb') IS NULL
    CREATE DATABASE PaymentsDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'payments_user')
    CREATE LOGIN payments_user
        WITH PASSWORD = '$(PAYMENTS_DB_PASSWORD)',
             DEFAULT_DATABASE = PaymentsDb,
             CHECK_POLICY = ON;
GO

USE PaymentsDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'payments_user')
    CREATE USER payments_user FOR LOGIN payments_user;
ALTER ROLE db_owner ADD MEMBER payments_user;
GO

-- ============================================================
-- Notifications  (Fase 4.6)
-- ============================================================
--
-- La quinta base, y la unica que no nacio en la Fase 0. Entra en 4.6 porque el
-- consumer de Notifications no puede cumplir la regla 6 sin una fila que
-- consultar — mismo motivo por el que Payments gano la suya en 3.5.
--
-- Que Notifications tenga base NO le da acceso a nada ajeno: sigue sin poder
-- leer OrdersDb, y por eso el CustomerEmail viaja dentro de OrderConfirmed y
-- OrderCancelled.
USE master;
GO

IF DB_ID('NotificationsDb') IS NULL
    CREATE DATABASE NotificationsDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'notifications_user')
    CREATE LOGIN notifications_user
        WITH PASSWORD = '$(NOTIFICATIONS_DB_PASSWORD)',
             DEFAULT_DATABASE = NotificationsDb,
             CHECK_POLICY = ON;
GO

USE NotificationsDb;
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'notifications_user')
    CREATE USER notifications_user FOR LOGIN notifications_user;
ALTER ROLE db_owner ADD MEMBER notifications_user;
GO

-- ============================================================
-- Resumen: lo que se vera en "docker compose logs db-init"
-- ============================================================
USE master;
GO

-- El CAST es solo cosmetico: sys.databases.name es sysname (nvarchar(128)) y
-- sqlcmd rellena la columna a su ancho completo, lo que hace el log ilegible.
SELECT CAST(d.name AS varchar(16))                        AS [database],
       CAST(ISNULL(p.name, '(sin login)') AS varchar(20)) AS [login]
FROM sys.databases d
LEFT JOIN sys.server_principals p
       ON p.name = LOWER(REPLACE(d.name, 'Db', '')) + '_user'
WHERE d.name IN ('CatalogDb', 'OrdersDb', 'InventoryDb', 'PaymentsDb', 'NotificationsDb')
ORDER BY d.name;
GO
