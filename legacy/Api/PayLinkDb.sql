-- ==============================================================
-- DROP & CREATE DATABASE
-- ==============================================================
DROP DATABASE IF EXISTS paylinkdb;
CREATE DATABASE paylinkdb CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
USE paylinkdb;

-- ==============================================================
-- 1. CLIENTS (Merchants integrating PayLink)
-- ==============================================================
CREATE TABLE clients (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    ClientId CHAR(36) NOT NULL DEFAULT (LOWER(UUID())) UNIQUE,
    Name VARCHAR(255) NOT NULL,
    OriginUrl VARCHAR(255) NOT NULL,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- ==============================================================
-- 2. USERS (End customers making payments)
-- ==============================================================
CREATE TABLE users (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    ClientId INT NOT NULL,
    Name VARCHAR(255),
    Email VARCHAR(255) NOT NULL,
    Mobile VARCHAR(20) NOT NULL,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT unique_email_mobile UNIQUE (Email, Mobile),
    FOREIGN KEY (ClientId) REFERENCES clients(Id) ON DELETE CASCADE
);

-- ==============================================================
-- 3. PAYMENT GATEWAYS (Configured per client)
-- ==============================================================
CREATE TABLE payment_gateways (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    Name VARCHAR(50) NOT NULL,            -- e.g., 'PhonePe', 'PayU', 'Cashfree'
    Status ENUM('ACTIVE','INACTIVE') DEFAULT 'INACTIVE',
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE tenant_payment_gateways (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    ClientId INT NOT NULL,
    PaymentGatewayId INT NOT NULL,
    Status ENUM('ACTIVE','INACTIVE') DEFAULT 'INACTIVE',
    Metadata JSON NULL,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY (ClientId) REFERENCES clients(Id) ON DELETE CASCADE,
    FOREIGN KEY (PaymentGatewayId) REFERENCES payment_gateways(Id) ON DELETE CASCADE,
    UNIQUE KEY uq_client_gateway (ClientId, PaymentGatewayId)
);

-- ==============================================================
-- 4. CURRENCIES
-- ==============================================================
CREATE TABLE currencies (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    Code VARCHAR(10) NOT NULL UNIQUE,   -- e.g. 'INR', 'USD'
    Symbol VARCHAR(5) NOT NULL,         -- e.g. ₹, $, €
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- ==============================================================
-- 6. PAYMENTS (Main transaction record)
-- ==============================================================
CREATE TABLE payments (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    PaymentUid CHAR(36) NOT NULL DEFAULT (LOWER(UUID())) UNIQUE,
    MerchantTransactionId VARCHAR(255) NOT NULL,
    ClientId INT NOT NULL,
    GatewayId INT NOT NULL,
    Status ENUM('Initiated', 'Pending', 'Processing', 'Failed', 'Completed') NOT NULL DEFAULT 'Initiated',
    Amount DECIMAL(12,2) NOT NULL CHECK (Amount > 0),
    CurrencyId INT NOT NULL,
    UserId INT NOT NULL,
    IsTest BOOLEAN DEFAULT FALSE,
    PaymentData JSON NULL,
    Metadata JSON NULL,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ExpiryAt TIMESTAMP NULL,
    FOREIGN KEY (UserId) REFERENCES users(Id) ON DELETE CASCADE,
    FOREIGN KEY (GatewayId) REFERENCES payment_gateways(Id) ON DELETE CASCADE,
    FOREIGN KEY (ClientId) REFERENCES clients(Id) ON DELETE CASCADE,
    FOREIGN KEY (CurrencyId) REFERENCES currencies(Id) ON DELETE CASCADE,
    UNIQUE KEY uq_client_merchant_txn (ClientId, MerchantTransactionId)
);

CREATE INDEX idx_payments_status ON payments (Status);
CREATE INDEX idx_payments_client_created ON payments (ClientId, CreatedAt);
 
-- ==============================================================
-- 7. WEBHOOKS (Incoming gateway notifications)
-- ==============================================================
CREATE TABLE webhooks (
    Id INT AUTO_INCREMENT PRIMARY KEY,
    PaymentId INT NOT NULL,
    GatewayId INT NOT NULL,
    WebhookData JSON NOT NULL,   -- stores raw JSON payload
    Metadata JSON NULL,          -- optional contextual info (verification results, headers, retry count, etc.)
    Processed BOOLEAN DEFAULT FALSE,
    CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (GatewayId) REFERENCES payment_gateways(Id) ON DELETE CASCADE,
    FOREIGN KEY (PaymentId) REFERENCES payments(Id) ON DELETE CASCADE
);

CREATE INDEX idx_webhooks_processed ON webhooks (Processed, CreatedAt);
