-- Phase 4: a tenant's own domain (e.g. pay.acme.in), proven with a DNS TXT record. SQLite.
CREATE TABLE IF NOT EXISTS tenant_domains (
    domain       VARCHAR(253)  NOT NULL PRIMARY KEY,
    tenant_id    BIGINT        NOT NULL,
    token        VARCHAR(64)   NOT NULL,
    status       VARCHAR(20)   NOT NULL,
    verified_at  VARCHAR(40)   NULL,
    checked_at   VARCHAR(40)   NULL,
    last_error   VARCHAR(300)  NULL,
    created_at   VARCHAR(40)   NOT NULL,
    created_by   VARCHAR(320)  NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_tenant_domains_tenant ON tenant_domains (tenant_id);
