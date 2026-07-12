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
