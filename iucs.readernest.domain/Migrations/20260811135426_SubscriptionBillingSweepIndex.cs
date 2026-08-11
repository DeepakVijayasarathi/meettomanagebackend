using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.readernest.domain.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionBillingSweepIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_status",
                table: "subscriptions");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_status_next_billing_at_utc",
                table: "subscriptions",
                columns: new[] { "status", "next_billing_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subscriptions_status_next_billing_at_utc",
                table: "subscriptions");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_status",
                table: "subscriptions",
                column: "status");
        }
    }
}
