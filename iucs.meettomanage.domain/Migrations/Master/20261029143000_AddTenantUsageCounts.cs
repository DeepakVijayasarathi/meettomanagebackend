using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master;

[DbContext(typeof(MasterDbContext))]
[Migration("20261029143000_AddTenantUsageCounts")]
public partial class AddTenantUsageCounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "sessions_month",
            table: "tenant_usage_snapshot",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "recordings_month",
            table: "tenant_usage_snapshot",
            type: "integer",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "sessions_month", table: "tenant_usage_snapshot");
        migrationBuilder.DropColumn(name: "recordings_month", table: "tenant_usage_snapshot");
    }
}
