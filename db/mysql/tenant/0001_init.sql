-- PayLink tenant database, MySQL 8.0+. One database per tenant.
-- Phase 0 holds the tenant's team, sign-in policy and audit log. Payments arrive in later migrations.

CREATE TABLE IF NOT EXISTS tenant_settings (
    skey        VARCHAR(100)  NOT NULL PRIMARY KEY,
    svalue      MEDIUMTEXT    NULL,
    updated_at  VARCHAR(40)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- People admitted to this tenant's console. Identity comes from the tenant's LoginLink organization.
CREATE TABLE IF NOT EXISTS members (
    id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    subject          VARCHAR(200)  NOT NULL,
    email            VARCHAR(320)  NULL,
    name             VARCHAR(200)  NULL,
    role             VARCHAR(20)   NOT NULL,     -- owner | admin | developer | finance | viewer
    status           VARCHAR(20)   NOT NULL,     -- approved | pending | removed
    source           VARCHAR(20)   NOT NULL,     -- signup | approved | auto
    created_at       VARCHAR(40)   NOT NULL,
    decided_by       VARCHAR(320)  NULL,
    decided_at       VARCHAR(40)   NULL,
    last_sign_in_at  VARCHAR(40)   NULL,
    UNIQUE KEY uq_members_subject (subject)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- LoginLink role name -> PayLink role, used by the "anyone holding a mapped role" admission mode.
CREATE TABLE IF NOT EXISTS role_map (
    loginlink_role  VARCHAR(100)  NOT NULL PRIMARY KEY,
    paylink_role    VARCHAR(20)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS audit_log (
    id          BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    at          VARCHAR(40)   NOT NULL,
    actor_type  VARCHAR(20)   NOT NULL,
    actor_id    VARCHAR(200)  NULL,
    actor_name  VARCHAR(320)  NULL,
    ip          VARCHAR(64)   NULL,
    action      VARCHAR(100)  NOT NULL,
    target      VARCHAR(300)  NULL,
    details     TEXT          NULL,
    prev_hash   VARCHAR(64)   NOT NULL,
    hash        VARCHAR(64)   NOT NULL,
    KEY ix_audit_at (at),
    KEY ix_audit_action (action)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT IGNORE INTO role_map (loginlink_role, paylink_role) VALUES
    ('paylink-owner', 'owner'),
    ('paylink-admin', 'admin'),
    ('paylink-developer', 'developer'),
    ('paylink-finance', 'finance'),
    ('paylink-viewer', 'viewer');

INSERT IGNORE INTO tenant_settings (skey, svalue, updated_at) VALUES
    ('admission_mode', 'auto_tenant_admins', '1970-01-01T00:00:00.000Z');
