-- Migration: AddReviews
-- Adds reviews and review_requests tables for the review generation system.
-- These are platform-level tables (not per-tenant) — they live on the marketing site.
-- Run this against the master database.

-- Reviews: public-facing social proof for the marketing site
CREATE TABLE IF NOT EXISTS reviews (
    id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    source                     varchar(50)  NOT NULL DEFAULT 'internal',
    rating                     smallint     NOT NULL CHECK (rating BETWEEN 1 AND 5),
    author_name                varchar(120) NOT NULL DEFAULT '',
    author_role                varchar(200),
    text                       text         NOT NULL DEFAULT '',
    response                   text,
    review_url                 varchar(1000),
    is_visible                 boolean      NOT NULL DEFAULT true,
    academy_tenant_id          uuid,
    -- BaseEntity fields (automatically managed by EF Core interceptor)
    created_at_utc             timestamptz  NOT NULL DEFAULT now(),
    updated_at_utc             timestamptz,
    is_deleted                 boolean      NOT NULL DEFAULT false,
    deleted_at_utc             timestamptz
);

-- Review requests: outbound invitations to customers to leave reviews
CREATE TABLE IF NOT EXISTS review_requests (
    id                         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    contact_name               varchar(200) NOT NULL DEFAULT '',
    contact_method             varchar(20)  NOT NULL DEFAULT '',  -- 'email' or 'whatsapp'
    contact_value              varchar(256) NOT NULL DEFAULT '',
    platform                   varchar(50)  NOT NULL DEFAULT '',  -- 'capterra', 'g2', 'google', etc.
    status                     varchar(20)  NOT NULL DEFAULT 'pending',  -- 'pending', 'sent', 'replied', 'declined', 'expired'
    sent_url                   varchar(1000),
    sent_at_utc                timestamptz,
    expires_at_utc             timestamptz,
    academy_tenant_id          uuid,
    -- BaseEntity fields
    created_at_utc             timestamptz  NOT NULL DEFAULT now(),
    updated_at_utc             timestamptz,
    is_deleted                 boolean      NOT NULL DEFAULT false,
    deleted_at_utc             timestamptz
);

-- Indexes for common queries
CREATE INDEX IF NOT EXISTS ix_reviews_visible_created
    ON reviews (is_visible, created_at_utc DESC, id) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS ix_reviews_academy_created
    ON reviews (academy_tenant_id, created_at_utc DESC, id) WHERE is_deleted = false;

CREATE INDEX IF NOT EXISTS ix_review_requests_pending
    ON review_requests (status, expires_at_utc, created_at_utc DESC, id)
    WHERE is_deleted = false AND status = 'pending';

CREATE INDEX IF NOT EXISTS ix_review_requests_academy
    ON review_requests (academy_tenant_id, created_at_utc DESC, id)
    WHERE is_deleted = false;
