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

-- ── Workflow Checkpoints ──
-- Stores agent execution state for durable workflows (MAF).
-- If a worker crashes mid-processing, the agent resumes from the last checkpoint.
CREATE TABLE IF NOT EXISTS workflow_checkpoints (
    id UUID PRIMARY KEY,
    agent_name VARCHAR(128) NOT NULL,
    document_id UUID NOT NULL,
    current_activity VARCHAR(128) NOT NULL,
    state_data TEXT,
    is_completed BOOLEAN NOT NULL DEFAULT FALSE,
    is_failed BOOLEAN NOT NULL DEFAULT FALSE,
    error_message TEXT,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_workflow_checkpoints_agent_document ON workflow_checkpoints(agent_name, document_id);
CREATE INDEX IF NOT EXISTS idx_workflow_checkpoints_completed ON workflow_checkpoints(is_completed);

-- ── Agent Definitions ──
-- Registry of available agents for dynamic discovery and orchestration.
-- New agents (Translation, NER, Summarization) are added here.
CREATE TABLE IF NOT EXISTS agent_definitions (
    id UUID PRIMARY KEY,
    name VARCHAR(128) NOT NULL UNIQUE,
    description TEXT NOT NULL DEFAULT '',
    handler_type VARCHAR(512) NOT NULL,
    activities JSONB NOT NULL DEFAULT '[]',
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_agent_definitions_name ON agent_definitions(name);

-- Seed the default DocumentProcessing agent
INSERT INTO agent_definitions (id, name, description, handler_type, activities) VALUES
    (gen_random_uuid(), 'DocumentProcessing', 'Downloads, parses, extracts text from PDF documents', 'Worker.Agents.DocumentProcessingAgent', '["DownloadDocument","ParseDocument","ExtractText","SaveResult","UpdateStatus"]')
ON CONFLICT (name) DO NOTHING;
