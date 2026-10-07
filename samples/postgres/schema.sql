-- Sample PostgreSQL schema for trying Weir end to end.
-- Run against a scratch database, then point a Weir data connection (Provider "PostgreSql") at it.
-- PostgreSQL has no table-valued parameters; the bulk import takes a jsonb argument instead.

CREATE TABLE IF NOT EXISTS widgets
(
    id         serial PRIMARY KEY,
    name       text           NOT NULL,
    price      numeric(10, 2) NOT NULL,
    created_at timestamptz    NOT NULL DEFAULT now()
);

-- Returns every widget (a set-returning function maps to a table-valued endpoint).
CREATE OR REPLACE FUNCTION get_widgets()
RETURNS SETOF widgets
LANGUAGE sql
AS $$
    SELECT id, name, price, created_at FROM widgets ORDER BY id;
$$;

-- Returns one widget by id (zero or one row).
CREATE OR REPLACE FUNCTION get_widget_by_id(p_id integer)
RETURNS SETOF widgets
LANGUAGE sql
AS $$
    SELECT id, name, price, created_at FROM widgets WHERE id = p_id;
$$;

-- Inserts a widget and returns the new id through an INOUT parameter (a stored procedure).
CREATE OR REPLACE PROCEDURE create_widget(p_name text, p_price numeric, INOUT new_id integer)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO widgets (name, price) VALUES (p_name, p_price) RETURNING id INTO new_id;
END;
$$;

-- Bulk-inserts widgets from a jsonb array of {"name":..., "price":...} objects; returns the count.
CREATE OR REPLACE FUNCTION import_widgets(items jsonb)
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    inserted integer;
BEGIN
    INSERT INTO widgets (name, price)
    SELECT elem->>'name', (elem->>'price')::numeric
    FROM jsonb_array_elements(items) AS elem;
    GET DIAGNOSTICS inserted = ROW_COUNT;
    RETURN inserted;
END;
$$;

-- Streaming check for PostgreSQL: a set-returning function that yields rows in batches, pausing between
-- them. Rows produced before a pause reach the client before it (Npgsql streams the reader), which is
-- what `weir-sample stream` measures. There is no endpoint seed for PostgreSQL - create the endpoint in
-- the admin UI: provider PostgreSql, object stream_widgets, result mode multi-row, delivery mode Stream.
CREATE OR REPLACE FUNCTION stream_widgets(
    p_batches        integer DEFAULT 5,
    p_rows_per_batch integer DEFAULT 500,
    p_delay_ms       integer DEFAULT 1000)
RETURNS TABLE (batch integer, seq integer, produced_at_utc timestamptz, payload text)
LANGUAGE plpgsql
AS $$
DECLARE
    b integer;
    s integer;
BEGIN
    IF p_batches NOT BETWEEN 1 AND 100
       OR p_rows_per_batch NOT BETWEEN 1 AND 10000
       OR p_delay_ms NOT BETWEEN 0 AND 10000 THEN
        RAISE EXCEPTION 'Batches must be 1-100, RowsPerBatch 1-10000 and DelayMs 0-10000.';
    END IF;

    FOR b IN 1..p_batches LOOP
        FOR s IN 1..p_rows_per_batch LOOP
            batch := b;
            seq := s;
            produced_at_utc := clock_timestamp();
            payload := repeat('x', 64);
            RETURN NEXT;
        END LOOP;

        IF b < p_batches AND p_delay_ms > 0 THEN
            PERFORM pg_sleep(p_delay_ms / 1000.0);
        END IF;
    END LOOP;
END;
$$;
