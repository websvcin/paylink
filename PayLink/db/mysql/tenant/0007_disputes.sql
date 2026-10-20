-- Phase 4: disputes (chargebacks) the gateways report. MySQL 8.0+.
-- Same as db/sqlite/tenant/0007_disputes.sql.

CREATE TABLE IF NOT EXISTS disputes (
    id            VARCHAR(40)    NOT NULL PRIMARY KEY,
    payment_id    VARCHAR(40)    NOT NULL,
    attempt_id    VARCHAR(40)    NULL,
    connector     VARCHAR(50)    NOT NULL,
    livemode      INT            NOT NULL,
    gateway_ref   VARCHAR(100)   NOT NULL,
    amount        BIGINT         NOT NULL,
    currency      VARCHAR(3)     NOT NULL,
    reason        VARCHAR(200)   NULL,
    status        VARCHAR(20)    NOT NULL,
    respond_by    VARCHAR(40)    NULL,
    note          VARCHAR(1000)  NULL,
    created_at    VARCHAR(40)    NOT NULL,
    updated_at    VARCHAR(40)    NOT NULL,
    UNIQUE KEY uq_disputes_gateway (connector, gateway_ref),
    KEY ix_disputes_payment (payment_id),
    KEY ix_disputes_status (livemode, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
