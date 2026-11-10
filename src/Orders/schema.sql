CREATE TABLE IF NOT EXISTS orders (
    id uuid PRIMARY KEY,
    request_id uuid NOT NULL UNIQUE,
    email text NOT NULL,
    total numeric(12,2) NOT NULL CHECK (total > 0),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS outbox (
    id uuid PRIMARY KEY,
    payload text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL
);
CREATE INDEX IF NOT EXISTS ix_outbox_pending ON outbox (created_at) WHERE published_at IS NULL;
