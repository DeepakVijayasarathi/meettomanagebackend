using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.readernest.domain.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionActiveUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_child_id",
                table: "subscriptions");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_child_id_package_plan_id",
                table: "subscriptions",
                columns: new[] { "child_id", "package_plan_id" },
                unique: true,
                filter: "\"status\" = 'Active' AND \"is_deleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_child_id_package_plan_id",
                table: "subscriptions");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_child_id",
                table: "subscriptions",
                column: "child_id");
        }
    }
}
