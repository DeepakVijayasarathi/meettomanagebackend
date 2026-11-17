using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "acknowledged_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_recap_sent_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_reminder_day_sent_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "demo_reminder_hour_sent_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "first_contacted_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "high_intent_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lost_reason",
                table: "platform_demo_requests",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stage",
                table: "platform_demo_requests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "New");

            migrationBuilder.AddColumn<DateTime>(
                name: "stage_changed_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tenant_code",
                table: "platform_demo_requests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "platform_demo_requests",
                type: "uuid",
                nullable: true);

            // Existing leads start at the stage their status implies; contacted ones keep their first contact time.
            migrationBuilder.Sql("""
                UPDATE platform_demo_requests SET
                    stage = CASE status WHEN 'Contacted' THEN 'Contacted' WHEN 'Converted' THEN 'Customer' WHEN 'Closed' THEN 'Lost' ELSE 'New' END,
                    first_contacted_at_utc = CASE WHEN status <> 'New' THEN last_contacted_at_utc END;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_platform_demo_requests_demo_at_utc",
                table: "platform_demo_requests",
                column: "demo_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_platform_demo_requests_stage",
                table: "platform_demo_requests",
                column: "stage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_platform_demo_requests_demo_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropIndex(
                name: "ix_platform_demo_requests_stage",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "acknowledged_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "demo_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "demo_recap_sent_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "demo_reminder_day_sent_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "demo_reminder_hour_sent_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "first_contacted_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "high_intent_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "lost_reason",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "stage",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "stage_changed_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "tenant_code",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "platform_demo_requests");
        }
    }
}
