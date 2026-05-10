-- Idempotent database initialization for PDF Processing System
-- This script can be safely re-run (uses CREATE TABLE IF NOT EXISTS)

-- Lookup table for human-readable status names.
-- Services use INTEGER status internally (no JOIN needed).
CREATE TABLE IF NOT EXISTS document_statuses (
    id INTEGER PRIMARY KEY,
    name VARCHAR(20) NOT NULL UNIQUE
);

-- Seed statuses (matching DocumentStatus enum values)
INSERT INTO document_statuses (id, name) VALUES
    (0, 'Uploaded'),
    (1, 'Processing'),
    (2, 'Completed'),
    (3, 'Failed')
ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS documents (
    id UUID PRIMARY KEY,
    filename VARCHAR(512) NOT NULL,
    status INTEGER NOT NULL DEFAULT 0 REFERENCES document_statuses(id),
    file_path VARCHAR(1024) NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    started_at TIMESTAMP WITH TIME ZONE,
    completed_at TIMESTAMP WITH TIME ZONE,
    error_message TEXT,
    extracted_text TEXT
);

CREATE TABLE IF NOT EXISTS outbox (
    id UUID PRIMARY KEY,
    document_id UUID NOT NULL REFERENCES documents(id),
    message_payload JSONB NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    processed_at TIMESTAMP WITH TIME ZONE,
    retry_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_outbox_unprocessed ON outbox(processed_at) WHERE processed_at IS NULL;

CREATE TABLE IF NOT EXISTS processed_messages (
    message_id UUID PRIMARY KEY,
    document_id UUID NOT NULL REFERENCES documents(id),
    processed_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_processed_messages_message_id ON processed_messages(message_id);
CREATE INDEX IF NOT EXISTS idx_documents_status ON documents(status);