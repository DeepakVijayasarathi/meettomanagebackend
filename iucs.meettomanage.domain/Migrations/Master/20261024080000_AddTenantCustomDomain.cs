using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using iucs.meettomanage.domain.Data;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master;

[DbContext(typeof(MasterDbContext))]
[Migration("20261024080000_AddTenantCustomDomain")]
public partial class AddTenantCustomDomain : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "custom_domain", table: "tenants", type: "character varying(253)",
            maxLength: 253, nullable: true);
        migrationBuilder.CreateIndex(
            name: "ix_tenants_custom_domain", table: "tenants", column: "custom_domain", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "ix_tenants_custom_domain", table: "tenants");
        migrationBuilder.DropColumn(name: "custom_domain", table: "tenants");
    }
}
