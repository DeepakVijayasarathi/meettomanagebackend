"""HTTP integration checks against an isolated PostgreSQL server.

Build the API first. Run with --pg-bin <PostgreSQL bin directory> --pg-port <port>.
Creates only uniquely named scratch databases and drops those in finally.
Never reads app connection strings or touches the operator's existing databases.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import base64
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--pg-bin", required=True)
    parser.add_argument("--pg-port", required=True, type=int)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    api = root / "iucs.meettomanage.api"
    dll = api / "bin/Debug/net9.0/iucs.meettomanage.api.dll"
    psql = str(Path(args.pg_bin) / ("psql.exe" if os.name == "nt" else "psql"))
    master = "mtm_test_master_" + uuid.uuid4().hex
    process = None
    log = tempfile.NamedTemporaryFile(prefix="mtm-http-", suffix=".log", delete=False)
    checks = 0

    def sql(database, command):
        result = subprocess.run([psql, "-X", "-h", "127.0.0.1", "-p", str(args.pg_port),
                                 "-U", "postgres", "-d", database, "-At", "-v", "ON_ERROR_STOP=1", "-c", command],
                                capture_output=True, text=True, check=True)
        return result.stdout.strip()

    def expect(condition, message):
        nonlocal checks
        if not condition:
            raise AssertionError(message)
        checks += 1

    try:
        sql("postgres", f'CREATE DATABASE "{master}"')
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        password = "Integration-only-platform-password"
        password_hash = subprocess.run(["dotnet", str(dll), "platform-password-hash"],
                                       input=password + "\n", capture_output=True, text=True, check=True).stdout.strip()
        server = f"Host=127.0.0.1;Port={args.pg_port};Username=postgres;Database=postgres;Maximum Pool Size=10"
        env = os.environ.copy()
        env.update({
            "ASPNETCORE_ENVIRONMENT": "Development", "DOTNET_ENVIRONMENT": "Development",
            "MultiTenancy__Enabled": "true", "ConnectionStrings__MasterDb": server.replace("Database=postgres", f"Database={master}"),
            "ConnectionStrings__TenantServer": server, "ConnectionStrings__TenantProvisioning": "",
            "Jwt__SigningKey": "integration-test-only-signing-key-at-least-32-bytes",
            "MultiTenancy__PlatformAdmin__Email": "platform@example.test",
            "MultiTenancy__PlatformAdmin__PasswordHash": password_hash,
            "Database__MigrateOnStartup": "true", "Storage__Provider": "Local",
            "Storage__LocalPath": tempfile.mkdtemp(prefix="mtm-uploads-"),
            "Storage__S3__Endpoint": "", "Storage__S3__AccessKey": "", "Storage__S3__SecretKey": "", "Storage__S3__BucketName": "",
            "Frontend__BaseUrl": "http://frontend.example.test", "Api__BaseUrl": base,
            "Crm__WebhookUrl": "", "Integrations__CrmWebhookUrl": "", "Gemini__ApiKey": "",
        })
        process = subprocess.Popen(["dotnet", str(dll), "--urls", base], cwd=api, env=env,
                                   stdout=log, stderr=subprocess.STDOUT)

        def request(path, token=None, method="GET", body=None):
            headers = {"Content-Type": "application/json"}
            if token:
                headers["Authorization"] = "Bearer " + token
            req = Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=headers, method=method)
            try:
                response = urlopen(req, timeout=90)
            except HTTPError as ex:
                response = ex
            text = response.read().decode()
            return response.code, json.loads(text) if text else None

        def wait_ready():
            for _ in range(180):
                if process.poll() is not None:
                    raise RuntimeError("API startup failed; see " + log.name)
                try:
                    if request("/health")[0] == 200:
                        return
                except OSError:
                    pass
                time.sleep(0.25)
            raise RuntimeError("API startup timed out; see " + log.name)
        wait_ready()

        status, login = request("/api/platform/login", method="POST", body={"email": "platform@example.test", "password": password})
        expect(status == 200, "Platform login failed")
        platform = login["accessToken"]
        tenants = []
        for code in ("academy-one", "academy-two"):
            status, tenant = request("/api/platform/tenants", platform, "POST", {
                "code": code, "name": code, "adminEmail": code + "@example.test", "adminPin": "4821",
            })
            expect(status == 200 and tenant["status"] == "Active", f"Provisioning {code} failed: {status} {tenant}")
            tenants.append(tenant)
        expect(len(sql(master, 'SELECT "DatabaseName" FROM tenants').splitlines()) == 2, "Two databases not registered")

        tokens, users = [], []
        for tenant in tenants:
            status, login = request("/api/auth/login", method="POST", body={"email": tenant["code"] + "@example.test", "pin": "4821"})
            expect(status == 200, "Username-only tenant login failed")
            tokens.append(login["accessToken"])
            users.append(login["user"]["id"])
            claims = json.loads(base64.urlsafe_b64decode(tokens[-1].split(".")[1] + "=="))
            expect(claims["tenant_id"] == tenant["id"] and claims["tenant_code"] == tenant["code"], "Tenant missing in signed JWT")
        expect(request("/api/platform/tenants", tokens[0])[0] == 403, "Tenant admin gained platform access")
        config_path = "/api/platform/tenants/" + tenants[0]["id"] + "/configuration"
        expect(request(config_path, tokens[0])[0] == 403, "Client admin gained operator configuration access")
        status, configuration = request(config_path, platform)
        expect(status == 200 and any(x["key"] == "org.name" for x in configuration), "Platform could not load selected tenant configuration")
        expect(request(config_path, platform, "PUT", [{"key": "org.name", "value": "Operator managed academy", "category": "General", "isPublic": True}])[0] == 200,
               "Platform could not update selected tenant configuration")
        first_settings = request("/t/academy-one/api/settings/public")[1]
        second_settings = request("/t/academy-two/api/settings/public")[1]
        expect(any(x["key"] == "org.name" and x["value"] == "Operator managed academy" for x in first_settings), "Configuration change missing in selected tenant")
        expect(all(x["key"] != "org.name" or x["value"] != "Operator managed academy" for x in second_settings), "Operator configuration update leaked into another tenant")
        menus_path = "/api/platform/tenants/" + tenants[0]["id"] + "/menus"
        menu = {"portal": "admin", "section": "Tenant tools", "sectionOrder": 20, "label": "Platform managed menu", "path": "/admin/test-platform-menu",
                "icon": "Settings", "sortOrder": 1, "isActive": True, "requiredModule": "Settings"}
        status, created_menu = request(menus_path, platform, "POST", menu)
        expect(status == 200, "Platform menu creation failed")
        second_menu_path = "/api/platform/tenants/" + tenants[1]["id"] + "/menus"
        expect(all(x["id"] != created_menu["id"] for x in request(second_menu_path, platform)[1]), "Platform menu create affected another tenant")
        expect(request(second_menu_path + "/" + created_menu["id"], platform, "PUT", menu)[0] == 404, "Menu ID crossed the selected tenant boundary")
        menu["label"] = "Updated operator menu"
        expect(request(menus_path + "/" + created_menu["id"], platform, "PUT", menu)[0] == 200, "Platform menu update failed")
        expect(request(menus_path + "/" + created_menu["id"], platform, "DELETE")[0] == 204, "Platform menu delete failed")
        expect(request(menus_path, tokens[0])[0] == 403, "Client token gained platform menu management")
        expect(request("/api/platform/tenants/" + tenants[0]["id"] + "/permission-modules", platform)[0] == 200, "Platform permission-module selector failed")
        expect(request("/api/departments", platform)[0] == 403, "Platform token gained tenant business access")
        expect(request("/t/academy-two/api/departments", tokens[0])[0] == 403, "URL changed an authenticated tenant")
        expect(request("/api/users/" + users[0], tokens[1])[0] == 404, "Tenant B accessed Tenant A user")
        expect(request("/api/monitoring/backups", tokens[0])[0] == 403, "Tenant admin accessed shared database backups")
        expect(request("/api/monitoring/servers/logs?serverName=app&container=api", tokens[0])[0] == 403, "Tenant admin accessed shared infrastructure logs")
        expect(request("/hubs/monitoring/negotiate?negotiateVersion=1", tokens[0], "POST")[0] == 403,
               "Tenant admin accessed the shared infrastructure monitoring stream")

        status, department = request("/api/departments", tokens[0], "POST", {"name": "Only academy one", "isActive": True})
        expect(status == 201, "Tenant A department creation failed")
        expect(any(x["name"] == "Only academy one" for x in request("/api/departments", tokens[0])[1]), "Tenant A cannot read its data")
        expect(all(x["name"] != "Only academy one" for x in request("/api/departments", tokens[1])[1]), "Tenant A data leaked into B")
        expect(request("/api/departments/" + department["id"], tokens[1], "PUT", {"name": "Cross tenant edit", "isActive": True})[0] == 404,
               "Tenant B edited Tenant A department")

        status, created = request("/api/users", tokens[0], "POST", {
            "email": "parent@example.test", "firstName": "Test parent", "phone": "+91 98765 43210", "role": "Parent",
        })
        expect(status == 201, f"User creation failed: {status} {created}")
        expect(request("/api/users", tokens[1], "POST", {
            "email": "parent@example.test", "firstName": "Duplicate", "role": "Parent",
        })[0] == 409, "Cross-tenant duplicate email accepted")
        expect(request("/api/users", tokens[1], "POST", {
            "email": "other@example.test", "firstName": "Duplicate phone", "phone": "9876543210", "role": "Parent",
        })[0] == 409, "Cross-tenant duplicate phone accepted")
        status, pin = request("/api/users/" + created["id"] + "/pin", tokens[0])
        expect(status == 200, "Could not retrieve initial test PIN")
        expect(request("/api/auth/login", method="POST", body={"email": "09876543210", "pin": pin["pin"]})[0] == 200, "Normalized mobile login failed")
        expect(request("/api/auth/change-pin", tokens[0], "POST", {"currentPin": "4821", "newPin": "6593"})[0] == 204, "PIN change failed")
        expect(request("/api/auth/login", method="POST", body={"email": "academy-one@example.test", "pin": "4821"})[0] == 401, "Old PIN still accepted")
        expect(request("/api/auth/login", method="POST", body={"email": "academy-one@example.test", "pin": "6593"})[0] == 200, "New PIN cannot log in")
        expect(sql(master, 'SELECT count(*) FROM tenant_login_accounts WHERE "PinHash" <> \'\'') == "3", "Central hashed credentials not synchronized")

        with ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(request, "/api/users", token, "POST", {
                "email": "racing@example.test", "firstName": "Concurrent registration", "role": "Parent",
            }) for token in tokens]
            statuses = sorted(future.result()[0] for future in futures)
        expect(statuses == [201, 409], "Concurrent duplicate registrations did not have exactly one winner")
        expect(sql(master, 'SELECT count(*) FROM tenant_login_identifiers WHERE "Identifier" = \'email:racing@example.test\'') == "1",
               "Concurrent registration created duplicate aliases")

        expect(request("/api/platform/tenants/" + tenants[0]["id"] + "/status", platform, "PUT", {"suspended": True})[0] == 204, "Suspension failed")
        expect(request("/api/departments", tokens[0])[0] == 401, "Suspended tenant token still accepted")
        expect(request("/api/auth/login", method="POST", body={"email": "academy-one@example.test", "pin": "6593"})[0] == 401, "Suspended tenant login accepted")
        expect(request("/api/departments", tokens[1])[0] == 200, "Suspending A affected B")
        expect(request("/api/platform/tenants/" + tenants[0]["id"] + "/synchronize-logins", platform, "POST")[0] == 204, "Directory rebuild failed")
        expect(request("/api/platform/tenants/" + tenants[0]["id"] + "/status", platform, "PUT", {"suspended": False})[0] == 204, "Reactivation failed")
        expect(request("/api/departments", tokens[0])[0] == 200, "Reactivated tenant cannot read its data")
        first_database = sql(master, f'SELECT "DatabaseName" FROM tenants WHERE "Id" = \'{tenants[0]["id"]}\'')
        if not re.fullmatch(r"mt_[a-f0-9]{32}", first_database):
            raise AssertionError("Unexpected scratch database identifier")
        sql("postgres", f'ALTER DATABASE "{first_database}" ALLOW_CONNECTIONS false')
        sql("postgres", f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{first_database}'")
        expect(request("/api/departments", tokens[1])[0] == 200, "Tenant database outage affected another tenant")
        process.terminate()
        process.wait(timeout=15)
        process = subprocess.Popen(["dotnet", str(dll), "--urls", base], cwd=api, env=env, stdout=log, stderr=subprocess.STDOUT)
        wait_ready()
        expect(request("/api/departments", tokens[1])[0] == 200, "One unavailable database prevented healthy-client startup")
        sql("postgres", f'ALTER DATABASE "{first_database}" ALLOW_CONNECTIONS true')
        expect(request("/api/auth/login", method="POST", body={"email": "academy-one@example.test", "pin": "6593"})[0] == 200,
               "A tenant's changed PIN did not survive restart")
        print(f"PASS: {checks} PostgreSQL/HTTP multi-tenant integration checks")
    finally:
        if process is not None:
            process.terminate()
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        log.close()
        try:
            databases = sql(master, 'SELECT "DatabaseName" FROM tenants').splitlines()
            for name in databases:
                if re.fullmatch(r"mt_[a-f0-9]{32}", name):
                    sql("postgres", f'DROP DATABASE IF EXISTS "{name}" WITH (FORCE)')
        finally:
            sql("postgres", f'DROP DATABASE IF EXISTS "{master}" WITH (FORCE)')
        print("API log:", log.name)


if __name__ == "__main__":
    main()
