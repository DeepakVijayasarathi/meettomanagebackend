START TRANSACTION;
CREATE SEQUENCE IF NOT EXISTS platform_invoice_number_seq START WITH 1 INCREMENT BY 1;

CREATE TABLE plan (
    id uuid NOT NULL,
    code character varying(50) NOT NULL,
    name character varying(150) NOT NULL,
    description character varying(1000),
    is_public boolean NOT NULL,
    is_active boolean NOT NULL,
    trial_days integer NOT NULL,
    sort_order integer NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_plan PRIMARY KEY (id)
);

CREATE TABLE platform_email_template (
    id uuid NOT NULL,
    key character varying(64) NOT NULL,
    subject character varying(300) NOT NULL,
    body text NOT NULL,
    is_active boolean NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_platform_email_template PRIMARY KEY (id)
);

CREATE TABLE platform_invoice (
    id uuid NOT NULL,
    invoice_number character varying(40) NOT NULL,
    tenant_id uuid NOT NULL,
    subscription_id uuid,
    period_start_utc timestamp with time zone NOT NULL,
    period_end_utc timestamp with time zone NOT NULL,
    subtotal numeric(12,2) NOT NULL,
    tax_rate_percent numeric(5,2) NOT NULL,
    tax_amount numeric(12,2) NOT NULL,
    total numeric(12,2) NOT NULL,
    currency character varying(3) NOT NULL,
    status character varying(16) NOT NULL,
    issued_at_utc timestamp with time zone NOT NULL,
    due_at_utc timestamp with time zone NOT NULL,
    paid_at_utc timestamp with time zone,
    target_plan_price_id uuid,
    gateway_order_ref character varying(128),
    payment_url character varying(1000),
    CONSTRAINT pk_platform_invoice PRIMARY KEY (id),
    CONSTRAINT fk_platform_invoice_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants ("Id") ON DELETE RESTRICT
);

CREATE TABLE platform_webhook_event (
    id uuid NOT NULL,
    provider character varying(40) NOT NULL,
    external_event_id character varying(128) NOT NULL,
    event_type character varying(80) NOT NULL,
    payload text NOT NULL,
    received_at_utc timestamp with time zone NOT NULL,
    processed_at_utc timestamp with time zone,
    CONSTRAINT pk_platform_webhook_event PRIMARY KEY (id)
);

CREATE TABLE subscription_reminder_log (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    subscription_id uuid NOT NULL,
    rule_key character varying(64) NOT NULL,
    cycle_key character varying(128) NOT NULL,
    channel character varying(20) NOT NULL,
    recipient character varying(256),
    status character varying(16) NOT NULL,
    attempts integer NOT NULL,
    error character varying(500),
    created_at_utc timestamp with time zone NOT NULL,
    sent_at_utc timestamp with time zone,
    CONSTRAINT pk_subscription_reminder_log PRIMARY KEY (id)
);

CREATE TABLE tenant_billing_profile (
    tenant_id uuid NOT NULL,
    legal_name character varying(200),
    billing_email character varying(256),
    billing_phone character varying(30),
    gstin character varying(20),
    address character varying(500),
    state character varying(60),
    country character varying(2) NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_tenant_billing_profile PRIMARY KEY (tenant_id),
    CONSTRAINT fk_tenant_billing_profile_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants ("Id") ON DELETE CASCADE
);

CREATE TABLE tenant_entitlement_override (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    key character varying(64) NOT NULL,
    value_type character varying(16) NOT NULL,
    value bigint NOT NULL,
    expires_at_utc timestamp with time zone,
    reason character varying(500),
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_tenant_entitlement_override PRIMARY KEY (id),
    CONSTRAINT fk_tenant_entitlement_override_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants ("Id") ON DELETE CASCADE
);

CREATE TABLE tenant_subscription_event (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    subscription_id uuid NOT NULL,
    event_type character varying(64) NOT NULL,
    detail character varying(1000),
    actor character varying(256) NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_tenant_subscription_event PRIMARY KEY (id)
);

CREATE TABLE tenant_usage_snapshot (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    snapshot_date date NOT NULL,
    active_students integer NOT NULL,
    staff_count integer NOT NULL,
    storage_bytes bigint NOT NULL,
    session_minutes_month integer NOT NULL,
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_tenant_usage_snapshot PRIMARY KEY (id)
);

CREATE TABLE plan_entitlement (
    id uuid NOT NULL,
    plan_id uuid NOT NULL,
    key character varying(64) NOT NULL,
    value_type character varying(16) NOT NULL,
    value bigint NOT NULL,
    CONSTRAINT pk_plan_entitlement PRIMARY KEY (id),
    CONSTRAINT fk_plan_entitlement_plan_plan_id FOREIGN KEY (plan_id) REFERENCES plan (id) ON DELETE CASCADE
);

CREATE TABLE plan_price (
    id uuid NOT NULL,
    plan_id uuid NOT NULL,
    billing_interval character varying(16) NOT NULL,
    currency character varying(3) NOT NULL,
    amount numeric(12,2) NOT NULL,
    gateway_plan_ref character varying(128),
    is_active boolean NOT NULL,
    effective_from_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_plan_price PRIMARY KEY (id),
    CONSTRAINT fk_plan_price_plan_plan_id FOREIGN KEY (plan_id) REFERENCES plan (id) ON DELETE CASCADE
);

CREATE TABLE platform_invoice_line (
    id uuid NOT NULL,
    invoice_id uuid NOT NULL,
    description character varying(300) NOT NULL,
    quantity integer NOT NULL,
    unit_amount numeric(12,2) NOT NULL,
    amount numeric(12,2) NOT NULL,
    CONSTRAINT pk_platform_invoice_line PRIMARY KEY (id),
    CONSTRAINT fk_platform_invoice_line_platform_invoice_invoice_id FOREIGN KEY (invoice_id) REFERENCES platform_invoice (id) ON DELETE CASCADE
);

CREATE TABLE platform_payment (
    id uuid NOT NULL,
    invoice_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    gateway character varying(40) NOT NULL,
    gateway_payment_ref character varying(128),
    amount numeric(12,2) NOT NULL,
    currency character varying(3) NOT NULL,
    status character varying(16) NOT NULL,
    failure_reason character varying(500),
    created_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_platform_payment PRIMARY KEY (id),
    CONSTRAINT fk_platform_payment_platform_invoice_invoice_id FOREIGN KEY (invoice_id) REFERENCES platform_invoice (id) ON DELETE RESTRICT
);

CREATE TABLE tenant_subscription (
    id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    plan_id uuid NOT NULL,
    plan_price_id uuid,
    status character varying(24) NOT NULL,
    trial_ends_at_utc timestamp with time zone,
    current_period_start_utc timestamp with time zone,
    current_period_end_utc timestamp with time zone,
    cancel_at_period_end boolean NOT NULL,
    cancelled_at_utc timestamp with time zone,
    grace_ends_at_utc timestamp with time zone,
    suspended_by_billing boolean NOT NULL,
    pending_plan_price_id uuid,
    gateway_customer_ref character varying(128),
    gateway_subscription_ref character varying(128),
    created_at_utc timestamp with time zone NOT NULL,
    updated_at_utc timestamp with time zone NOT NULL,
    CONSTRAINT pk_tenant_subscription PRIMARY KEY (id),
    CONSTRAINT fk_tenant_subscription_plan_plan_id FOREIGN KEY (plan_id) REFERENCES plan (id) ON DELETE RESTRICT,
    CONSTRAINT fk_tenant_subscription_plan_price_plan_price_id FOREIGN KEY (plan_price_id) REFERENCES plan_price (id) ON DELETE RESTRICT,
    CONSTRAINT fk_tenant_subscription_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants ("Id") ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ux_plan_code ON plan (code);

CREATE UNIQUE INDEX ux_plan_entitlement_plan_id_key ON plan_entitlement (plan_id, key);

CREATE INDEX ix_plan_price_plan_id_billing_interval_is_active ON plan_price (plan_id, billing_interval, is_active);

CREATE UNIQUE INDEX ux_platform_email_template_key ON platform_email_template (key);

CREATE INDEX ix_platform_invoice_gateway_order_ref ON platform_invoice (gateway_order_ref);

CREATE INDEX ix_platform_invoice_status_due_at_utc ON platform_invoice (status, due_at_utc);

CREATE INDEX ix_platform_invoice_tenant_id_issued_at_utc ON platform_invoice (tenant_id, issued_at_utc);

CREATE UNIQUE INDEX ux_platform_invoice_invoice_number ON platform_invoice (invoice_number);

CREATE INDEX ix_platform_invoice_line_invoice_id ON platform_invoice_line (invoice_id);

CREATE INDEX ix_platform_payment_gateway_gateway_payment_ref ON platform_payment (gateway, gateway_payment_ref);

CREATE INDEX ix_platform_payment_invoice_id ON platform_payment (invoice_id);

CREATE INDEX ix_platform_payment_tenant_id ON platform_payment (tenant_id);

CREATE UNIQUE INDEX ux_platform_webhook_event_provider_external_event_id ON platform_webhook_event (provider, external_event_id);

CREATE INDEX ix_subscription_reminder_log_tenant_id_created_at_utc ON subscription_reminder_log (tenant_id, created_at_utc);

CREATE UNIQUE INDEX ux_subscription_reminder_log_subscription_id_rule_key_cycle_key_channel ON subscription_reminder_log (subscription_id, rule_key, cycle_key, channel);

CREATE UNIQUE INDEX ux_tenant_entitlement_override_tenant_id_key ON tenant_entitlement_override (tenant_id, key);

CREATE INDEX ix_tenant_subscription_plan_id ON tenant_subscription (plan_id);

CREATE INDEX ix_tenant_subscription_plan_price_id ON tenant_subscription (plan_price_id);

CREATE INDEX ix_tenant_subscription_status_current_period_end_utc ON tenant_subscription (status, current_period_end_utc);

CREATE INDEX ix_tenant_subscription_trial_ends_at_utc ON tenant_subscription (trial_ends_at_utc);

CREATE UNIQUE INDEX ux_tenant_subscription_tenant_id ON tenant_subscription (tenant_id);

CREATE INDEX ix_tenant_subscription_event_tenant_id_created_at_utc ON tenant_subscription_event (tenant_id, created_at_utc);

CREATE UNIQUE INDEX ux_tenant_usage_snapshot_tenant_id_snapshot_date ON tenant_usage_snapshot (tenant_id, snapshot_date);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261020141611_AddSaasBilling', '9.0.4');

COMMIT;

