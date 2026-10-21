using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddTenantAppAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every tenant that exists today keeps working in the mobile app; only tenants created from now on
            // carry the operator's explicit choice. (The default lives here, not in the model, so EF never treats
            // an explicit "not granted" as "unset".)
            migrationBuilder.AddColumn<bool>(
                name: "app_access_granted",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "app_access_granted",
                table: "tenants");
        }
    }
}
