-- HyFib HealthCare360: the two PostgreSQL accounts of the hospital database (docs/deployment/DEPLOYMENT.md, "Two database
-- accounts"). Run by Install-HC360Server.ps1 and Restore-HC360.ps1 as the PostgreSQL superuser, connected to the hospital
-- database, after both accounts exist:
--
--   psql -d hc360 -v ON_ERROR_STOP=1 -c "SELECT set_config('hc360.owner_role', 'hc360_owner', false), set_config('hc360.app_role', 'hc360_app', false)" -f hc360-database-roles.sql
--
-- The integration tests run the same file against a throwaway database (DatabaseRolesTests). Running it again changes
-- nothing that is already right.
--
-- * The owner (hc360_owner) owns the database and every schema, table, sequence and function in it: start-up applies the
--   migrations and makes the audit trail's triggers as it, and the jobs that add monthly partitions sign in as it.
-- * The account the server works as (hc360_app) reads and writes rows: SELECT, INSERT, UPDATE, DELETE on the tables, the
--   sequences their keys come from, and EXECUTE on the functions. It owns nothing, so it can neither change a table nor drop
--   or turn off a trigger: the audit trail stays append-only whatever runs as it (its triggers refuse UPDATE of a sealed
--   event, DELETE and TRUNCATE). A table the owner makes later gets the same grants (ALTER DEFAULT PRIVILEGES).
-- * A database that one account (hc360_app) owned, as installed before the split, is handed to the owner as it is
--   (REASSIGN OWNED); nothing in it is copied or changed.

DO $roles$
DECLARE
    owner_role text := current_setting('hc360.owner_role');
    app_role text := current_setting('hc360.app_role');
    db text := current_database();
    s record;
BEGIN
    IF owner_role = app_role THEN
        RAISE EXCEPTION 'the owner and the working account must be two accounts, not %', owner_role;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = owner_role) OR NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_role) THEN
        RAISE EXCEPTION 'make the accounts % and % first', owner_role, app_role;
    END IF;

    -- Neither may do more than its part: no superuser, no making roles or databases, no replication, no row security bypass.
    -- The working account is not a member of the owner, so it cannot SET ROLE to it.
    EXECUTE format('ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', owner_role);
    EXECUTE format('ALTER ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', app_role);
    IF pg_has_role(app_role, owner_role, 'MEMBER') THEN
        EXECUTE format('REVOKE %I FROM %I', owner_role, app_role);
    END IF;

    -- Whatever the working account owned here (the whole database, before the split) now belongs to the owner.
    EXECUTE format('ALTER DATABASE %I OWNER TO %I', db, owner_role);
    EXECUTE format('REASSIGN OWNED BY %I TO %I', app_role, owner_role);

    -- Only these two connect.
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', db);
    EXECUTE format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO %I', db, app_role);
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;

    -- What is there now: every schema the owner has (public, the modules' and audit), with its tables, sequences and functions.
    FOR s IN
        SELECT n.nspname FROM pg_namespace n
        WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
          AND (pg_get_userbyid(n.nspowner) = owner_role OR n.nspname = 'public')
    LOOP
        EXECUTE format('REVOKE CREATE ON SCHEMA %I FROM %I', s.nspname, app_role);
        EXECUTE format('GRANT USAGE ON SCHEMA %I TO %I', s.nspname, app_role);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %I TO %I', s.nspname, app_role);
        EXECUTE format('REVOKE TRUNCATE, REFERENCES, TRIGGER ON ALL TABLES IN SCHEMA %I FROM %I', s.nspname, app_role);
        EXECUTE format('GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %I TO %I', s.nspname, app_role);
        EXECUTE format('GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA %I TO %I', s.nspname, app_role);
    END LOOP;

    -- What the owner makes later: the migrations' new schemas and tables, and the months the jobs add.
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT USAGE ON SCHEMAS TO %I', owner_role, app_role);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', owner_role, app_role);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO %I', owner_role, app_role);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I GRANT EXECUTE ON FUNCTIONS TO %I', owner_role, app_role);
END
$roles$;
