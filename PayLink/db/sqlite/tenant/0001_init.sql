-- PayLink tenant database, SQLite. Same tables as db/mysql/tenant/0001_init.sql.

CREATE TABLE IF NOT EXISTS tenant_settings (
    skey        VARCHAR(100) NOT NULL PRIMARY KEY,
    svalue      TEXT         NULL,
    updated_at  VARCHAR(40)  NOT NULL
);

CREATE TABLE IF NOT EXISTS members (
    id               INTEGER      PRIMARY KEY AUTOINCREMENT,
    subject          VARCHAR(200) NOT NULL UNIQUE,
    email            VARCHAR(320) NULL,
    name             VARCHAR(200) NULL,
    role             VARCHAR(20)  NOT NULL,
    status           VARCHAR(20)  NOT NULL,
    source           VARCHAR(20)  NOT NULL,
    created_at       VARCHAR(40)  NOT NULL,
    decided_by       VARCHAR(320) NULL,
    decided_at       VARCHAR(40)  NULL,
    last_sign_in_at  VARCHAR(40)  NULL
);

CREATE TABLE IF NOT EXISTS role_map (
    loginlink_role  VARCHAR(100) NOT NULL PRIMARY KEY,
    paylink_role    VARCHAR(20)  NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_log (
    id          INTEGER      PRIMARY KEY AUTOINCREMENT,
    at          VARCHAR(40)  NOT NULL,
    actor_type  VARCHAR(20)  NOT NULL,
    actor_id    VARCHAR(200) NULL,
    actor_name  VARCHAR(320) NULL,
    ip          VARCHAR(64)  NULL,
    action      VARCHAR(100) NOT NULL,
    target      VARCHAR(300) NULL,
    details     TEXT         NULL,
    prev_hash   VARCHAR(64)  NOT NULL,
    hash        VARCHAR(64)  NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_audit_at ON audit_log (at);
CREATE INDEX IF NOT EXISTS ix_audit_action ON audit_log (action);

INSERT OR IGNORE INTO role_map (loginlink_role, paylink_role) VALUES
    ('paylink-owner', 'owner'),
    ('paylink-admin', 'admin'),
    ('paylink-developer', 'developer'),
    ('paylink-finance', 'finance'),
    ('paylink-viewer', 'viewer');

INSERT OR IGNORE INTO tenant_settings (skey, svalue, updated_at) VALUES
    ('admission_mode', 'auto_tenant_admins', '1970-01-01T00:00:00.000Z');
