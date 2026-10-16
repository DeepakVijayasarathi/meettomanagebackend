CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE tenants (
    "Id" uuid NOT NULL,
    "Code" character varying(50) NOT NULL,
    "Name" character varying(200) NOT NULL,
    "DatabaseName" character varying(63) NOT NULL,
    "Status" character varying(24) NOT NULL,
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "ProvisionedAtUtc" timestamp with time zone,
    CONSTRAINT "PK_tenants" PRIMARY KEY ("Id")
);

CREATE TABLE tenant_login_accounts (
    "UserId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_tenant_login_accounts" PRIMARY KEY ("UserId"),
    CONSTRAINT "AK_tenant_login_accounts_UserId_TenantId" UNIQUE ("UserId", "TenantId"),
    CONSTRAINT "FK_tenant_login_accounts_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES tenants ("Id") ON DELETE RESTRICT
);

CREATE TABLE tenant_login_identifiers (
    "Identifier" character varying(320) NOT NULL,
    "UserId" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    CONSTRAINT "PK_tenant_login_identifiers" PRIMARY KEY ("Identifier"),
    CONSTRAINT "FK_tenant_login_identifiers_tenant_login_accounts_UserId_Tenan~" FOREIGN KEY ("UserId", "TenantId") REFERENCES tenant_login_accounts ("UserId", "TenantId") ON DELETE RESTRICT
);

CREATE INDEX "IX_tenant_login_accounts_TenantId" ON tenant_login_accounts ("TenantId");

CREATE INDEX "IX_tenant_login_identifiers_TenantId_UserId" ON tenant_login_identifiers ("TenantId", "UserId");

CREATE INDEX "IX_tenant_login_identifiers_UserId_TenantId" ON tenant_login_identifiers ("UserId", "TenantId");

CREATE UNIQUE INDEX "IX_tenants_Code" ON tenants ("Code");

CREATE UNIQUE INDEX "IX_tenants_DatabaseName" ON tenants ("DatabaseName");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261015164401_InitialMasterRegistry', '9.0.4');

ALTER TABLE tenant_login_accounts ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE tenant_login_accounts ADD "PinHash" character varying(512) NOT NULL DEFAULT '';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261015171405_AddLoginDirectoryCredentials', '9.0.4');

COMMIT;
