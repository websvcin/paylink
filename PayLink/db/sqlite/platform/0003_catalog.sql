-- Phase 3: the connector catalog (what the platform installed and allows, per mode) and console addresses per tenant. SQLite.
-- Same as db/mysql/platform/0003_catalog.sql. ALTER TABLE runs once; PayLink records applied scripts.

CREATE TABLE IF NOT EXISTS connector_catalog (
    connector_id      VARCHAR(50)   NOT NULL PRIMARY KEY,
    state             VARCHAR(20)   NOT NULL DEFAULT 'installed',
    allow_test        INTEGER       NOT NULL DEFAULT 1,
    allow_live        INTEGER       NOT NULL DEFAULT 1,
    max_amount        BIGINT        NULL,
    source            VARCHAR(20)   NOT NULL DEFAULT 'builtin',
    package_id        VARCHAR(200)  NULL,
    package_version   VARCHAR(50)   NULL,
    previous_version  VARCHAR(50)   NULL,
    updated_at        VARCHAR(40)   NOT NULL,
    updated_by        VARCHAR(320)  NULL
);

ALTER TABLE tenants ADD COLUMN console_mode VARCHAR(20) NOT NULL DEFAULT 'path';
