-- Phase 4: disputes (chargebacks) the gateways report. SQLite.
-- Same as db/mysql/tenant/0007_disputes.sql.

CREATE TABLE IF NOT EXISTS disputes (
    id            VARCHAR(40)    NOT NULL PRIMARY KEY,
    payment_id    VARCHAR(40)    NOT NULL,
    attempt_id    VARCHAR(40)    NULL,
    connector     VARCHAR(50)    NOT NULL,
    livemode      INTEGER        NOT NULL,
    gateway_ref   VARCHAR(100)   NOT NULL,
    amount        BIGINT         NOT NULL,
    currency      VARCHAR(3)     NOT NULL,
    reason        VARCHAR(200)   NULL,
    status        VARCHAR(20)    NOT NULL,
    respond_by    VARCHAR(40)    NULL,
    note          VARCHAR(1000)  NULL,
    created_at    VARCHAR(40)    NOT NULL,
    updated_at    VARCHAR(40)    NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_disputes_gateway ON disputes (connector, gateway_ref);
CREATE INDEX IF NOT EXISTS ix_disputes_payment ON disputes (payment_id);
CREATE INDEX IF NOT EXISTS ix_disputes_status ON disputes (livemode, status);
