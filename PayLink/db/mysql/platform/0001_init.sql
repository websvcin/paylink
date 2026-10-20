-- PayLink platform database, MySQL 8.0+.
-- Applied automatically by PayLink (setup wizard or startup). A DBA can also run it by hand; see db/README.md.
-- Times are stored as ISO-8601 UTC strings (e.g. 2026-10-19T18:02:11.123Z) so the same queries work on SQLite and MySQL.

CREATE TABLE IF NOT EXISTS settings (
    skey        VARCHAR(100)  NOT NULL PRIMARY KEY,
    svalue      MEDIUMTEXT    NULL,
    updated_at  VARCHAR(40)   NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- The tenant registry: who exists, where each tenant's own database is, and how its team signs in.
CREATE TABLE IF NOT EXISTS tenants (
    id                        BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    slug                      VARCHAR(40)   NOT NULL,
    name                      VARCHAR(200)  NOT NULL,
    status                    VARCHAR(20)   NOT NULL DEFAULT 'active',      -- active | suspended
    db_mode                   VARCHAR(20)   NOT NULL,                       -- sqlite | auto | byo
    db_engine                 VARCHAR(20)   NOT NULL,                       -- Sqlite | MySql
    db_display                VARCHAR(300)  NOT NULL,                       -- description without secrets
    db_connection_enc         TEXT          NOT NULL,                       -- encrypted connection string
    db_provider_ref_enc       TEXT          NULL,                           -- encrypted, to remove an auto-provisioned database
    loginlink_org             VARCHAR(100)  NOT NULL,
    loginlink_issuer          VARCHAR(300)  NOT NULL,
    loginlink_mode            VARCHAR(20)   NOT NULL,                       -- existing | created
    console_app_id            VARCHAR(50)   NULL,
    console_client_id         VARCHAR(200)  NOT NULL,
    console_client_secret_enc TEXT          NOT NULL,
    mgmt_client_id            VARCHAR(200)  NULL,
    mgmt_client_secret_enc    TEXT          NULL,
    api_registration          VARCHAR(20)   NOT NULL DEFAULT 'skipped',     -- ok | failed | skipped
    api_registration_error    TEXT          NULL,
    created_at                VARCHAR(40)   NOT NULL,
    created_by                VARCHAR(320)  NULL,
    created_via               VARCHAR(20)   NOT NULL,                       -- signup | admin | api
    created_by_key_id         BIGINT        NULL,
    UNIQUE KEY uq_tenants_slug (slug),
    UNIQUE KEY uq_tenants_loginlink_org (loginlink_org)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- People admitted to /admin. Identity comes from LoginLink (subject = token "sub").
CREATE TABLE IF NOT EXISTS platform_admins (
    id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    subject          VARCHAR(200)  NOT NULL,
    email            VARCHAR(320)  NULL,
    name             VARCHAR(200)  NULL,
    access           VARCHAR(20)   NOT NULL DEFAULT 'admin',   -- admin | support
    status           VARCHAR(20)   NOT NULL,                   -- approved | pending | removed
    source           VARCHAR(20)   NOT NULL,                   -- setup | approved | auto
    created_at       VARCHAR(40)   NOT NULL,
    decided_by       VARCHAR(320)  NULL,
    decided_at       VARCHAR(40)   NULL,
    last_sign_in_at  VARCHAR(40)   NULL,
    UNIQUE KEY uq_platform_admins_subject (subject)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- LoginLink organizations allowed to have a MySQL database created for them at signup.
CREATE TABLE IF NOT EXISTS autoprov_approvals (
    id          BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    org_slug    VARCHAR(100)  NOT NULL,
    note        VARCHAR(300)  NULL,
    created_at  VARCHAR(40)   NOT NULL,
    created_by  VARCHAR(320)  NULL,
    used_at     VARCHAR(40)   NULL,
    UNIQUE KEY uq_autoprov_org (org_slug)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Platform developer keys (pdk_). Only a SHA-256 hash of the key is stored.
CREATE TABLE IF NOT EXISTS developer_keys (
    id              BIGINT         NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name            VARCHAR(200)   NOT NULL,
    key_prefix      VARCHAR(20)    NOT NULL,
    key_hash        VARCHAR(64)    NOT NULL,
    capabilities    VARCHAR(500)   NOT NULL,
    reach           VARCHAR(20)    NOT NULL DEFAULT 'all',   -- all | selected | own
    tenant_ids      VARCHAR(1000)  NULL,
    creation_limit  INT            NULL,
    expires_at      VARCHAR(40)    NULL,
    created_at      VARCHAR(40)    NOT NULL,
    created_by      VARCHAR(320)   NULL,
    revoked_at      VARCHAR(40)    NULL,
    last_used_at    VARCHAR(40)    NULL,
    UNIQUE KEY uq_developer_keys_hash (key_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Short-lived state for a tenant signup that spans a LoginLink sign-in redirect.
CREATE TABLE IF NOT EXISTS signup_sessions (
    id           VARCHAR(64)  NOT NULL PRIMARY KEY,
    payload_enc  MEDIUMTEXT   NOT NULL,
    created_at   VARCHAR(40)  NOT NULL,
    expires_at   VARCHAR(40)  NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

-- Every platform action. Each row's hash covers the previous row's hash, so editing a row breaks the chain.
CREATE TABLE IF NOT EXISTS audit_log (
    id          BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
    at          VARCHAR(40)   NOT NULL,
    actor_type  VARCHAR(20)   NOT NULL,     -- person | key | system
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
