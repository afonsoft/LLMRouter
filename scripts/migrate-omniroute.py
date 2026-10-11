#!/usr/bin/env python3
"""Migra dados de um banco OmniRoute (SQLite) para um banco LLMRouter.

Uso:
    python3 scripts/migrate-omniroute.py OMNI_DB LLMR_DB [--dry-run]
        [--no-usage] [--no-call-logs] [--no-kv]

- OMNI_DB: arquivo .db do OmniRoute (diegosouzapw/OmniRoute).
- LLMR_DB: arquivo .db do LLMRouter (criado/atualizado pelo servidor;
  rode o app uma vez antes para criar o schema).

O script é idempotente (INSERT OR IGNORE por Id). Tokens e segredos das
conexões são copiados para a coluna `Data` — trate o arquivo de destino
como sensível.

A senha de admin do OmniRoute é bcrypt e não é convertida — use
--admin-password SENHA para gravar um hash pbkdf2 no destino
(settings.adminPasswordHash), senão o login do dashboard fica
impossível quando o destino já tem hash.

Credenciais das conexões migram como ciphertext `enc:v1:` (AES-256-GCM).
O LLMRouter as decifra automaticamente quando a env
STORAGE_ENCRYPTION_KEY tem a MESMA chave usada no OmniRoute — sem ela,
as conexões migram mas não autenticam (re-autenticar no dashboard).

Requer SQLite >= 3.40. Se o seu Python for antigo (sqlite < 3.40) o
schema do OmniRoute pode falhar ao abrir (objetos FTS com sintaxe nova);
nesse caso `pip install pysqlite3-binary` é usado automaticamente quando
disponível.
"""

import argparse
import json
import sys
from datetime import datetime, timezone


def _connect(path, ro=False):
    try:
        import pysqlite3 as sqlite3  # type: ignore
    except ImportError:
        import sqlite3  # type: ignore
    uri = f"file:{path}?mode=ro" if ro else path
    return sqlite3.connect(uri, uri=ro)


def j(v):
    """JSON dump compacto ou None."""
    if v is None:
        return None
    return json.dumps(v, ensure_ascii=False, separators=(",", ":"))


def parse_json(v, default=None):
    if not v:
        return default
    try:
        return json.loads(v)
    except Exception:
        return default


def iso_to_ms(v):
    """'2026-09-28T18:28:23.596Z' ou epoch (s|ms) -> epoch ms (int) ou None."""
    if v is None or v == "":
        return None
    if isinstance(v, (int, float)):
        n = int(v)
        return n if n > 10**12 else n * 1000
    s = str(v).strip()
    try:
        n = int(s)
        return n if n > 10**12 else n * 1000
    except ValueError:
        pass
    try:
        return int(
            datetime.fromisoformat(s.replace("Z", "+00:00"))
            .astimezone(timezone.utc)
            .timestamp()
            * 1000
        )
    except ValueError:
        return None


def rows(conn, sql, args=()):
    cur = conn.execute(sql, args)
    cols = [d[0] for d in cur.description]
    return [dict(zip(cols, r)) for r in cur.fetchall()]


def table_exists(conn, name):
    try:
        conn.execute(f'SELECT 1 FROM "{name}" LIMIT 0')
        return True
    except Exception:
        return False


REPORT = {}


def report(name, n):
    REPORT[name] = REPORT.get(name, 0) + n


def migrate_connections(src, dst, dry):
    if not table_exists(src, "provider_connections"):
        return
    rs = rows(src, "SELECT * FROM provider_connections")
    for r in rs:
        data = {}
        for src_col, key in (
            ("access_token", "accessToken"),
            ("refresh_token", "refreshToken"),
            ("id_token", "idToken"),
            ("api_key", "apiKey"),
            ("scope", "scope"),
            ("project_id", "projectId"),
            ("token_type", "tokenType"),
            ("display_name", "displayName"),
            ("default_model", "defaultModel"),
            ("test_status", "testStatus"),
            ("last_error", "lastError"),
            ("last_error_at", "lastErrorAt"),
            ("last_error_type", "lastErrorType"),
            ("last_error_source", "lastErrorSource"),
            ("backoff_level", "backoffLevel"),
            ("rate_limited_until", "rateLimitedUntil"),
            ("health_check_interval", "healthCheckInterval"),
            ("last_health_check_at", "lastHealthCheckAt"),
            ("last_tested", "lastTested"),
            ("consecutive_use_count", "consecutiveUseCount"),
            ("rate_limit_protection", "rateLimitProtection"),
            ("last_used_at", "lastUsedAt"),
            ("group", "group"),
            ("max_concurrent", "maxConcurrent"),
            ("proxy_enabled", "proxyEnabled"),
            ("per_key_proxy_enabled", "perKeyProxyEnabled"),
            ("quota_visible", "quotaVisible"),
            ("last_ping_at", "lastPingAt"),
            ("last_pinged_reset_key", "lastPingedResetKey"),
            ("synced_models_at", "syncedModelsAt"),
            ("global_priority", "globalPriority"),
        ):
            v = r.get(src_col)
            if v is not None and v != "":
                data[key] = v
        exp = iso_to_ms(r.get("expires_at") or r.get("token_expires_at"))
        if exp is not None:
            data["expiresAt"] = exp
        elif r.get("expires_in"):
            data["expiresIn"] = r["expires_in"]
        for src_col, key in (
            ("provider_specific_data", "providerSpecificData"),
            ("quota_window_thresholds_json", "quotaWindowThresholds"),
            ("rate_limit_overrides_json", "rateLimitOverrides"),
        ):
            v = parse_json(r.get(src_col))
            if v is not None:
                data[key] = v
        if not dry:
            dst.execute(
                "INSERT OR IGNORE INTO providerConnections"
                " (Id,Provider,AuthType,Name,Email,Priority,IsActive,Data,CreatedAt,UpdatedAt)"
                " VALUES (?,?,?,?,?,?,?,?,?,?)",
                (
                    r["id"], r.get("provider") or "", r.get("auth_type") or "",
                    r.get("name"), r.get("email"), r.get("priority"),
                    r.get("is_active", 1), j(data) or "{}",
                    r.get("created_at") or "", r.get("updated_at") or "",
                ),
            )
    report("providerConnections", len(rs))


def migrate_api_keys(src, dst, dry):
    if not table_exists(src, "api_keys"):
        return
    rs = rows(src, "SELECT * FROM api_keys")
    for r in rs:
        allowed = parse_json(r.get("allowed_models"), []) or []
        restricted = 1 if allowed else 0
        allow = ",".join(str(m) for m in allowed) if allowed else None
        active = r.get("is_active", 1) and not r.get("is_banned", 0)
        if not dry:
            dst.execute(
                "INSERT OR IGNORE INTO apiKeys"
                " (Id,Key,Name,MachineId,IsActive,CreatedAt,AccessRestricted,AccessAllow)"
                " VALUES (?,?,?,?,?,?,?,?)",
                (
                    r["id"], r.get("key") or "", r.get("name"),
                    r.get("machine_id"), active, r.get("created_at") or "",
                    restricted, allow,
                ),
            )
    report("apiKeys", len(rs))


def migrate_combos(src, dst, dry):
    if not table_exists(src, "combos"):
        return
    rs = rows(src, "SELECT * FROM combos")
    skipped_extras = 0
    for r in rs:
        d = parse_json(r.get("data"), {}) or {}
        models = [
            m["model"] if isinstance(m, dict) and m.get("model") else str(m)
            for m in (d.get("models") or [])
        ]
        if r.get("system_message") or r.get("tool_filter_regex"):
            skipped_extras += 1
        if not dry:
            dst.execute(
                "INSERT OR IGNORE INTO combos"
                " (Id,Name,Kind,StickyLimit,Models,AllowedProviders,AllowedFamilies,Tiers,CreatedAt,UpdatedAt)"
                " VALUES (?,?,?,?,?,?,?,?,?,?)",
                (
                    r["id"], r.get("name") or d.get("name") or "",
                    d.get("kind") or "round-robin",
                    d.get("stickyLimit") or d.get("sticky_limit") or 1,
                    j(models) or "[]",
                    j(d.get("allowedProviders") or d.get("allowed_providers")),
                    j(d.get("allowedModelFamilies") or d.get("allowedFamilies")),
                    j(d.get("tiers")),
                    r.get("created_at") or "", r.get("updated_at") or "",
                ),
            )
    report("combos", len(rs))
    if skipped_extras:
        report("combos: extras ignorados (system_message/tool_filter_regex)", skipped_extras)


def migrate_provider_nodes(src, dst, dry):
    if not table_exists(src, "provider_nodes"):
        return
    rs = rows(src, "SELECT * FROM provider_nodes")
    for r in rs:
        data = {}
        for src_col, key in (
            ("prefix", "prefix"), ("api_type", "apiType"), ("base_url", "baseUrl"),
            ("chat_path", "chatPath"), ("models_path", "modelsPath"),
            ("icon_url", "iconUrl"),
            ("daily_quota_reset_timezone", "dailyQuotaResetTimezone"),
            ("daily_quota_reset_hour", "dailyQuotaResetHour"),
        ):
            v = r.get(src_col)
            if v is not None and v != "":
                data[key] = v
        headers = parse_json(r.get("custom_headers_json"))
        if headers is not None:
            data["customHeaders"] = headers
        if not dry:
            dst.execute(
                "INSERT OR IGNORE INTO providerNodes (Id,Type,Name,Data,CreatedAt,UpdatedAt)"
                " VALUES (?,?,?,?,?,?)",
                (
                    r["id"], r.get("type"), r.get("name"), j(data) or "{}",
                    r.get("created_at") or "", r.get("updated_at") or "",
                ),
            )
    report("providerNodes", len(rs))


def migrate_usage(src, dst, dry):
    if not table_exists(src, "usage_history"):
        return
    rs = rows(src, "SELECT * FROM usage_history")
    if not dry:
        dst.executemany(
            "INSERT INTO usageHistory"
            " (Timestamp,Provider,Model,ConnectionId,ApiKey,Endpoint,PromptTokens,"
            " CompletionTokens,Cost,LatencyMs,Status,Tokens,Meta) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
            [
                (
                    r.get("timestamp") or "", r.get("provider"), r.get("model"),
                    r.get("connection_id"), r.get("api_key_name"), r.get("endpoint"),
                    r.get("tokens_input") or 0, r.get("tokens_output") or 0, 0.0,
                    r.get("latency_ms") or 0, r.get("status"),
                    j({k: r[c] for c, k in (
                        ("tokens_cache_read", "cacheRead"), ("tokens_cache_creation", "cacheCreation"),
                        ("tokens_reasoning", "reasoning"), ("ttft_ms", "ttftMs")) if r.get(c)}),
                    j({k: r[c] for c, k in (
                        ("service_tier", "serviceTier"), ("success", "success"),
                        ("error_code", "errorCode"), ("combo_strategy", "comboStrategy"),
                        ("account_key", "accountKey"), ("account_label", "accountLabel"),
                        ("account_label_priority", "accountLabelPriority"),
                        ("cpa_auth_index", "cpaAuthIndex"), ("api_key_id", "apiKeyId")) if r.get(c)}),
                )
                for r in rs
            ],
        )
    report("usageHistory", len(rs))


def migrate_call_logs(src, dst, dry):
    if not table_exists(src, "call_logs"):
        return
    rs = rows(src, "SELECT * FROM call_logs")
    if not dry:
        dst.executemany(
            "INSERT OR IGNORE INTO requestDetails"
            " (Id,Timestamp,Provider,Model,ConnectionId,Status,Data) VALUES (?,?,?,?,?,?,?)",
            [
                (
                    r.get("correlation_id") or r.get("response_id") or str(r["id"]),
                    r.get("timestamp") or "", r.get("provider"), r.get("model"),
                    r.get("connection_id"), str(r.get("status") or ""),
                    j(r),
                )
                for r in rs
            ],
        )
    report("requestDetails(call_logs)", len(rs))


def migrate_kv(src, dst, dry):
    if not table_exists(src, "key_value"):
        return
    rs = rows(src, "SELECT * FROM key_value")
    if not dry:
        dst.executemany(
            "INSERT OR IGNORE INTO kv (Scope,Key,Value) VALUES (?,?,?)",
            [(r["namespace"], r["key"], r["value"]) for r in rs],
        )
    report("kv(key_value)", len(rs))


def migrate_jobs(src, dst, dry):
    if not table_exists(src, "jobs"):
        return
    rs = rows(src, "SELECT * FROM jobs")
    if not dry:
        dst.executemany(
            "INSERT OR IGNORE INTO jobStates (Id,Enabled) VALUES (?,?)",
            [(r["id"], r.get("enabled", 1)) for r in rs],
        )
    report("jobStates", len(rs))


def migrate_audit(src, dst, dry):
    if not table_exists(src, "audit_log"):
        return
    rs = rows(src, "SELECT * FROM audit_log")
    if not dry:
        dst.executemany(
            "INSERT INTO auditEvents (Actor,Action,Target,Meta,Ip,At) VALUES (?,?,?,?,?,?)",
            [
                (
                    r.get("actor"), r.get("action"), r.get("target"),
                    j({"details": parse_json(r.get("details"), r.get("details")),
                       "metadata": parse_json(r.get("metadata"), r.get("metadata")),
                       "resourceType": r.get("resource_type"),
                       "status": r.get("status"), "requestId": r.get("request_id")}),
                    r.get("ip_address"), r.get("timestamp") or "",
                )
                for r in rs
            ],
        )
    report("auditEvents", len(rs))


def migrate_admin_password(dst, password, dry):
    """A senha admin do OmniRoute vive em key_value['password'] como bcrypt —
    incompatível com o pbkdf2 do LLMRouter (settings.adminPasswordHash).
    Com --admin-password grava um hash novo; sem ele só avisa."""
    if password is None:
        row = dst.execute("SELECT Data FROM settings WHERE Id=1").fetchone()
        has_hash = row and '"adminPasswordHash"' in (row[0] or "")
        if has_hash:
            print("AVISO: settings.adminPasswordHash já existe e NÃO veio do OmniRoute;")
            print("       se o hash for desconhecido, o login do dashboard vai falhar.")
        print("Dica: --admin-password SENHA grava um hash pbkdf2 válido no destino.")
        return
    import base64
    import hashlib
    import os

    iters = 100_000
    salt = os.urandom(16)
    h = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, iters)
    ph = f"pbkdf2${iters}${base64.b64encode(salt).decode()}${base64.b64encode(h).decode()}"
    if not dry:
        row = dst.execute("SELECT Data FROM settings WHERE Id=1").fetchone()
        data = json.loads(row[0]) if row and row[0] else {}
        data["setupComplete"] = True
        data["adminPasswordHash"] = ph
        dst.execute(
            "INSERT INTO settings (Id,Data) VALUES (1,?) "
            "ON CONFLICT(Id) DO UPDATE SET Data=excluded.Data",
            (json.dumps(data),),
        )
    report("settings(admin)", 1)


def main():
    ap = argparse.ArgumentParser(description="Migra OmniRoute DB -> LLMRouter DB")
    ap.add_argument("omni_db")
    ap.add_argument("llmr_db")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--no-usage", action="store_true", help="pula usage_history")
    ap.add_argument("--no-call-logs", action="store_true", help="pula call_logs")
    ap.add_argument("--no-kv", action="store_true", help="pula key_value")
    ap.add_argument("--admin-password", metavar="SENHA",
                    help="grava adminPasswordHash pbkdf2 no destino")
    args = ap.parse_args()

    src = _connect(args.omni_db, ro=True)
    dst = _connect(args.llmr_db)
    try:
        migrate_connections(src, dst, args.dry_run)
        migrate_api_keys(src, dst, args.dry_run)
        migrate_combos(src, dst, args.dry_run)
        migrate_provider_nodes(src, dst, args.dry_run)
        migrate_jobs(src, dst, args.dry_run)
        migrate_audit(src, dst, args.dry_run)
        if not args.no_usage:
            migrate_usage(src, dst, args.dry_run)
        if not args.no_call_logs:
            migrate_call_logs(src, dst, args.dry_run)
        if not args.no_kv:
            migrate_kv(src, dst, args.dry_run)
        migrate_admin_password(dst, args.admin_password, args.dry_run)
        if args.dry_run:
            dst.rollback()
            print("[dry-run] nada foi escrito")
        else:
            dst.commit()
    finally:
        src.close()
        dst.close()
    for k, v in REPORT.items():
        print(f"{k}: {v}")


if __name__ == "__main__":
    sys.exit(main())
