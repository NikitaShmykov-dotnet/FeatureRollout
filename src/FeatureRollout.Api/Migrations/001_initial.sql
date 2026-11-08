CREATE TABLE IF NOT EXISTS flag_environments (
    name text PRIMARY KEY,
    revision bigint NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS flag_publications (
    environment text NOT NULL REFERENCES flag_environments(name),
    revision bigint NOT NULL,
    snapshot json NOT NULL,
    actor text NOT NULL,
    published_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (environment, revision)
);
