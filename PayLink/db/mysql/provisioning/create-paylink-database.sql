-- Manual provisioning for a PayLink MySQL database, for a DBA who prefers to create it by hand.
-- Use it for the platform database, or for a tenant that brings its own MySQL ("Use my own MySQL").
--
-- 1. Replace the three placeholders below, then run this file as a MySQL admin (root or similar):
--      mysql -u root -p < create-paylink-database.sql
-- 2. Give PayLink the database name, user and password (or a connection string) in the setup wizard
--    or the tenant signup page. PayLink creates its tables automatically on first connection.
--    To create the tables yourself instead, run, in order:
--      db/mysql/platform/*.sql   (platform database)
--      db/mysql/tenant/*.sql     (tenant database)
--
-- Placeholders:
--   paylink_example   database name: letters, digits and underscores only
--   paylink_user      user name, at most 32 characters
--   CHANGE_ME         a long random password
-- Replace '%' with the PayLink server's address to allow connections only from it.

CREATE DATABASE IF NOT EXISTS `paylink_example` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

CREATE USER IF NOT EXISTS 'paylink_user'@'%' IDENTIFIED BY 'CHANGE_ME';

-- PayLink needs to create and change its own tables (migrations) and read and write rows.
GRANT ALL PRIVILEGES ON `paylink_example`.* TO 'paylink_user'@'%';

FLUSH PRIVILEGES;
