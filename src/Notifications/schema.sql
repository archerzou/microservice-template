CREATE TABLE IF NOT EXISTS inbox (
    event_id uuid PRIMARY KEY,
    payload_hash text NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS notifications (
    id uuid PRIMARY KEY,
    order_id uuid NOT NULL UNIQUE,
    email text NOT NULL,
    subject text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
