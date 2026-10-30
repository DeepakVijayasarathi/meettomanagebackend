using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <summary>
    /// Marketing lead operations: campaign budgets, report settings, lead activity log and the
    /// lead ownership/archive/reminder columns. The attribution and follow-up columns
    /// (source, campaign, next_follow_up_at_utc, ...) come from AddMarketingLeadPipeline.
    /// This file was originally generated against an empty snapshot and re-created every
    /// table, which failed on existing databases; it now holds only the real delta.
    /// </summary>
    public partial class AddMarketingOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketing_campaign_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    spend = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    won_revenue = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_campaign_budgets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketing_report_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    last_sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_report_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketing_lead_activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_demo_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_lead_activities", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketing_lead_activities_platform_demo_requests_platform_dem",
                        column: x => x.platform_demo_request_id,
                        principalTable: "platform_demo_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_marketing_lead_activities_platform_demo_request_id",
                table: "marketing_lead_activities",
                column: "platform_demo_request_id");

            migrationBuilder.AddColumn<Guid>(
                name: "assigned_to_user_id",
                table: "platform_demo_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_reminder_sent_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_platform_demo_requests_archived_at_utc",
                table: "platform_demo_requests",
                column: "archived_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_platform_demo_requests_assigned_to_user_id",
                table: "platform_demo_requests",
                column: "assigned_to_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "ix_platform_demo_requests_assigned_to_user_id", table: "platform_demo_requests");
            migrationBuilder.DropIndex(name: "ix_platform_demo_requests_archived_at_utc", table: "platform_demo_requests");
            migrationBuilder.DropColumn(name: "last_reminder_sent_at_utc", table: "platform_demo_requests");
            migrationBuilder.DropColumn(name: "archived_at_utc", table: "platform_demo_requests");
            migrationBuilder.DropColumn(name: "assigned_to_user_id", table: "platform_demo_requests");
            migrationBuilder.DropTable(name: "marketing_lead_activities");
            migrationBuilder.DropTable(name: "marketing_report_settings");
            migrationBuilder.DropTable(name: "marketing_campaign_budgets");
        }
    }
}
