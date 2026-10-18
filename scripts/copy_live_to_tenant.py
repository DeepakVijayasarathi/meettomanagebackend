#!/usr/bin/env python3
"""Copy the data of a live (single-tenant) database into a tenant database.

The SOURCE (live) database is only ever READ: its session is opened read-only
(`default_transaction_read_only=on`) inside one REPEATABLE READ snapshot, so the
copy is consistent and nothing in the live database can be changed by this script.

The TARGET (tenant) database is replaced in one transaction: the tables that exist
in both databases are emptied and refilled from the source; if anything fails the
transaction is rolled back and the target is left untouched. The target keeps its own
schema and its `__EFMigrationsHistory` (run the API once afterwards so any pending
migration is applied), and tables that exist only in the target are not touched.

Safety rules
  * Dry run is the default. Nothing is written without --execute.
  * The target must be named mt_<32 hex> and must not be the source database.
  * The tenant must be Suspended in the master registry (checked unless
    --skip-status-check is given).
  * Back up the target database first (pg_dump) -- the copy replaces its data.

Password: set PGPASSWORD (or you are prompted). Never put it on the command line.

Example
  PGPASSWORD=... python copy_live_to_tenant.py --host 93.127.194.128 --user postgres \
      --source-db meettomanagedemo --target-db mt_1c2d9f5d52c34f2aa6ecee4b3f017154
  ... add --execute once the dry-run plan looks right.

Afterwards: restart the API (applies pending migrations), then use "Sync imported
logins" in the platform console while the tenant is still Suspended, then activate it.
"""
import argparse
import getpass
import os
import re
import sys
import tempfile

import psycopg2
from psycopg2 import sql

TARGET_NAME = re.compile(r"^mt_[0-9a-f]{32}$")
SKIP_TABLES = {"__EFMigrationsHistory"}


def connect(host, port, user, password, dbname, read_only):
    conn = psycopg2.connect(host=host, port=port, user=user, password=password,
                            dbname=dbname, connect_timeout=15,
                            options="-c default_transaction_read_only=on" if read_only else "")
    if read_only:
        conn.set_session(readonly=True, isolation_level="REPEATABLE READ")
    return conn


def tables_with_columns(cur):
    cur.execute("""
        SELECT c.table_name, c.column_name, c.is_nullable, c.column_default,
               c.is_generated, c.identity_generation
        FROM information_schema.columns c
        JOIN information_schema.tables t
          ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
        ORDER BY c.table_name, c.ordinal_position""")
    out = {}
    for table, col, nullable, default, generated, identity in cur.fetchall():
        out.setdefault(table, []).append({
            "name": col, "required": nullable == "NO" and default is None and identity is None,
            "generated": generated == "ALWAYS", "identity_always": identity == "ALWAYS"})
    return out


def count(cur, table):
    cur.execute(sql.SQL("SELECT count(*) FROM {}").format(sql.Identifier(table)))
    return cur.fetchone()[0]


def plan(src_cur, tgt_cur):
    src, tgt = tables_with_columns(src_cur), tables_with_columns(tgt_cur)
    copy, notes = [], []
    for table in sorted(set(src) & set(tgt)):
        if table in SKIP_TABLES:
            continue
        tgt_cols = {c["name"]: c for c in tgt[table]}
        shared = [c["name"] for c in src[table]
                  if c["name"] in tgt_cols and not tgt_cols[c["name"]]["generated"]]
        for c in src[table]:
            if c["name"] not in tgt_cols:
                notes.append(f"{table}.{c['name']}: only in source, not copied")
        for name, c in tgt_cols.items():
            if name not in {s["name"] for s in src[table]} and c["required"]:
                notes.append(f"{table}.{name}: NOT NULL without default in target but missing in source -> copy will fail")
            if c["identity_always"] and name in shared:
                notes.append(f"{table}.{name}: GENERATED ALWAYS identity -> copy will fail")
        copy.append((table, shared))
    for table in sorted(set(src) - set(tgt) - SKIP_TABLES):
        notes.append(f"{table}: only in source, not copied")
    for table in sorted(set(tgt) - set(src) - SKIP_TABLES):
        notes.append(f"{table}: only in target, left untouched")
    return copy, notes


def check_tenant_suspended(args, password):
    conn = connect(args.target_host, args.port, args.user, password, args.master_db, True)
    try:
        cur = conn.cursor()
        cur.execute('SELECT "Status" FROM tenants WHERE "DatabaseName" = %s', (args.target_db,))
        row = cur.fetchone()
    finally:
        conn.close()
    if row is None:
        sys.exit(f"{args.target_db} is not registered in {args.master_db}. Refusing to continue.")
    if row[0] != "Suspended":
        sys.exit(f"The tenant is {row[0]}. Suspend it in the platform console before copying.")


def reset_sequences(cur):
    cur.execute("""
        SELECT c.table_name, c.column_name, pg_get_serial_sequence(format('%I.%I', c.table_schema, c.table_name), c.column_name)
        FROM information_schema.columns c WHERE c.table_schema = 'public'""")
    for table, col, seq in cur.fetchall():
        if not seq:
            continue
        cur.execute(sql.SQL("SELECT max({}) FROM {}").format(sql.Identifier(col), sql.Identifier(table)))
        top = cur.fetchone()[0]
        if top is not None:
            cur.execute("SELECT setval(%s, %s)", (seq, top))


def orphan_report(cur, tables):
    cur.execute("""
        SELECT con.conname, child.relname, parent.relname,
               (SELECT array_agg(a.attname ORDER BY k.ord) FROM unnest(con.conkey) WITH ORDINALITY k(attnum, ord)
                  JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum),
               (SELECT array_agg(a.attname ORDER BY k.ord) FROM unnest(con.confkey) WITH ORDINALITY k(attnum, ord)
                  JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)
        FROM pg_constraint con
        JOIN pg_class child ON child.oid = con.conrelid
        JOIN pg_class parent ON parent.oid = con.confrelid
        JOIN pg_namespace n ON n.oid = child.relnamespace AND n.nspname = 'public'
        WHERE con.contype = 'f'""")
    problems = []
    for name, child, parent, ccols, pcols in cur.fetchall():
        if child not in tables:
            continue
        cond = sql.SQL(" AND ").join(sql.SQL("c.{} IS NOT NULL").format(sql.Identifier(x)) for x in ccols)
        join = sql.SQL(" AND ").join(sql.SQL("p.{} = c.{}").format(sql.Identifier(p), sql.Identifier(c))
                                    for c, p in zip(ccols, pcols))
        cur.execute(sql.SQL("SELECT count(*) FROM {} c WHERE {} AND NOT EXISTS (SELECT 1 FROM {} p WHERE {})").format(
            sql.Identifier(child), cond, sql.Identifier(parent), join))
        bad = cur.fetchone()[0]
        if bad:
            problems.append(f"{child} -> {parent} ({name}): {bad} orphan row(s)")
    return problems


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", required=True)
    ap.add_argument("--port", type=int, default=5432)
    ap.add_argument("--user", required=True)
    ap.add_argument("--source-host", help="defaults to --host")
    ap.add_argument("--target-host", help="defaults to --host")
    ap.add_argument("--source-db", required=True, help="the live database (read only)")
    ap.add_argument("--target-db", required=True, help="the tenant database, mt_<32 hex>")
    ap.add_argument("--master-db", default="meettomanage_master")
    ap.add_argument("--skip-status-check", action="store_true")
    ap.add_argument("--execute", action="store_true", help="really write to the target (default: dry run)")
    ap.add_argument("--yes", action="store_true", help="do not ask for confirmation")
    args = ap.parse_args()
    args.source_host = args.source_host or args.host
    args.target_host = args.target_host or args.host

    if not TARGET_NAME.match(args.target_db):
        sys.exit("The target must be a generated tenant database named mt_<32 hex characters>.")
    if (args.source_host, args.source_db) == (args.target_host, args.target_db):
        sys.exit("Source and target are the same database.")
    password = os.environ.get("PGPASSWORD") or getpass.getpass("PostgreSQL password: ")

    if args.execute and not args.skip_status_check:
        check_tenant_suspended(args, password)

    src = connect(args.source_host, args.port, args.user, password, args.source_db, True)
    tgt = connect(args.target_host, args.port, args.user, password, args.target_db, not args.execute)
    try:
        src_cur, tgt_cur = src.cursor(), tgt.cursor()
        copy, notes = plan(src_cur, tgt_cur)

        print(f"{'table':50} {'source rows':>12} {'target rows (replaced)':>24}")
        counts = {}
        for table, _ in copy:
            counts[table] = count(src_cur, table)
            print(f"{table:50} {counts[table]:>12} {count(tgt_cur, table):>24}")
        for note in notes:
            print("NOTE:", note)
        if not args.execute:
            print("\nDry run only - nothing was written. Re-run with --execute to copy.")
            return
        if any("copy will fail" in n for n in notes):
            sys.exit("Fix the notes above before copying.")
        if not args.yes and input(f"\nReplace the data in {args.target_db}? Type its name to confirm: ") != args.target_db:
            sys.exit("Cancelled.")

        tgt_cur.execute("SET session_replication_role = replica")  # skip FK triggers while loading
        names = sql.SQL(", ").join(sql.Identifier(t) for t, _ in copy)
        tgt_cur.execute(sql.SQL("TRUNCATE {} RESTART IDENTITY CASCADE").format(names))
        for table, cols in copy:
            col_sql = sql.SQL(", ").join(sql.Identifier(c) for c in cols)
            with tempfile.SpooledTemporaryFile(max_size=64 * 1024 * 1024) as buf:
                src_cur.copy_expert(sql.SQL("COPY (SELECT {} FROM {}) TO STDOUT").format(
                    col_sql, sql.Identifier(table)).as_string(src), buf)
                buf.seek(0)
                tgt_cur.copy_expert(sql.SQL("COPY {} ({}) FROM STDIN").format(
                    sql.Identifier(table), col_sql).as_string(tgt), buf)
        reset_sequences(tgt_cur)
        tgt_cur.execute("SET session_replication_role = DEFAULT")

        bad = [t for t, _ in copy if count(tgt_cur, t) != counts[t]]
        if bad:
            raise RuntimeError("Row counts differ after copy for: " + ", ".join(bad))
        problems = orphan_report(tgt_cur, {t for t, _ in copy})
        tgt.commit()
        print(f"\nCopied {len(copy)} tables into {args.target_db}; row counts match the live database.")
        for problem in problems:
            print("WARNING (also present in the live data):", problem)
        print("Next: restart the API, run 'Sync imported logins' while the tenant is Suspended, then activate it.")
    except Exception:
        tgt.rollback()
        print("\nFAILED - the target transaction was rolled back; the target is unchanged.", file=sys.stderr)
        raise
    finally:
        src.rollback()  # nothing was ever written to the source
        src.close()
        tgt.close()


if __name__ == "__main__":
    main()
