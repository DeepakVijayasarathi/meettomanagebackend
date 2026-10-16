using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master
{
    /// <inheritdoc />
    public partial class InitialMasterRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DatabaseName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProvisionedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_login_accounts",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_login_accounts", x => x.UserId);
                    table.UniqueConstraint("AK_tenant_login_accounts_UserId_TenantId", x => new { x.UserId, x.TenantId });
                    table.ForeignKey(
                        name: "FK_tenant_login_accounts_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tenant_login_identifiers",
                columns: table => new
                {
                    Identifier = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_login_identifiers", x => x.Identifier);
                    table.ForeignKey(
                        name: "FK_tenant_login_identifiers_tenant_login_accounts_UserId_Tenan~",
                        columns: x => new { x.UserId, x.TenantId },
                        principalTable: "tenant_login_accounts",
                        principalColumns: new[] { "UserId", "TenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_login_accounts_TenantId",
                table: "tenant_login_accounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_login_identifiers_TenantId_UserId",
                table: "tenant_login_identifiers",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_login_identifiers_UserId_TenantId",
                table: "tenant_login_identifiers",
                columns: new[] { "UserId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Code",
                table: "tenants",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenants_DatabaseName",
                table: "tenants",
                column: "DatabaseName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_login_identifiers");

            migrationBuilder.DropTable(
                name: "tenant_login_accounts");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
