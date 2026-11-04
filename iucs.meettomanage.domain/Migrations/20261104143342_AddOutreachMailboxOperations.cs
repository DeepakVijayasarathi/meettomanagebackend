using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddOutreachMailboxOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outreach_mailbox_sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    messages_checked = table.Column<int>(type: "integer", nullable: false),
                    replies_imported = table.Column<int>(type: "integer", nullable: false),
                    duplicates_skipped = table.Column<int>(type: "integer", nullable: false),
                    unmatched_skipped = table.Column<int>(type: "integer", nullable: false),
                    failed_messages = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_mailbox_sync_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outreach_unmatched_emails",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    from = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    thread_references = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    received_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    prospect_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_unmatched_emails", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_prospect_activities_external_id",
                table: "outreach_prospect_activities",
                column: "external_id",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_mailbox_sync_runs_started_at_utc",
                table: "outreach_mailbox_sync_runs",
                column: "started_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_unmatched_emails_external_id",
                table: "outreach_unmatched_emails",
                column: "external_id",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_unmatched_emails_resolved_at_utc",
                table: "outreach_unmatched_emails",
                column: "resolved_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outreach_mailbox_sync_runs");

            migrationBuilder.DropTable(
                name: "outreach_unmatched_emails");

            migrationBuilder.DropIndex(
                name: "ix_outreach_prospect_activities_external_id",
                table: "outreach_prospect_activities");
        }
    }
}
