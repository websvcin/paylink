-- Round 5: partner onboarding (referral link or a short form per payment method; tenants' requests). PostgreSQL 13+.
CREATE TABLE IF NOT EXISTS partner_programs (
    connector_id  VARCHAR(50)    NOT NULL PRIMARY KEY,
    mode          VARCHAR(10)    NOT NULL DEFAULT 'off',
    headline      VARCHAR(120)   NULL,
    pitch         VARCHAR(500)   NULL,
    link_url      VARCHAR(1000)  NULL,
    updated_at    VARCHAR(40)    NOT NULL,
    updated_by    VARCHAR(320)   NULL
);

CREATE TABLE IF NOT EXISTS partner_requests (
    id             BIGSERIAL      NOT NULL PRIMARY KEY,
    tenant_id      BIGINT         NOT NULL,
    connector_id   VARCHAR(50)    NOT NULL,
    kind           VARCHAR(10)    NOT NULL,
    status         VARCHAR(20)    NOT NULL DEFAULT 'new',
    contact_name   VARCHAR(200)   NULL,
    contact_email  VARCHAR(320)   NULL,
    contact_phone  VARCHAR(40)    NULL,
    details        TEXT           NULL,
    admin_note     VARCHAR(1000)  NULL,
    created_at     VARCHAR(40)    NOT NULL,
    created_by     VARCHAR(320)   NULL,
    updated_at     VARCHAR(40)    NULL,
    updated_by     VARCHAR(320)   NULL
);
CREATE INDEX IF NOT EXISTS ix_partner_requests_tenant ON partner_requests (tenant_id, connector_id);
CREATE INDEX IF NOT EXISTS ix_partner_requests_status ON partner_requests (status, created_at);
