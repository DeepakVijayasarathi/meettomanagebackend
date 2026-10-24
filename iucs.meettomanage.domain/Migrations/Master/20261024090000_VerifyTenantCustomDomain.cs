using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master;

[DbContext(typeof(MasterDbContext))]
[Migration("20261024090000_VerifyTenantCustomDomain")]
public sealed class VerifyTenantCustomDomain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<DateTime>(
        name: "custom_domain_verified_at_utc", table: "tenants", type: "timestamp with time zone", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "custom_domain_verified_at_utc", table: "tenants");
}
