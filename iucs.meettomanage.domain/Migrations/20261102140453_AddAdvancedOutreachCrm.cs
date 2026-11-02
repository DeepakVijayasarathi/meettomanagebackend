using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAdvancedOutreachCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "assigned_to",
                table: "outreach_prospects",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "lead_score",
                table: "outreach_prospects",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_action_at_utc",
                table: "outreach_prospects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pipeline_stage",
                table: "outreach_prospects",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "score_reason",
                table: "outreach_prospects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outreach_prospect_activities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    prospect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    detail = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    created_by = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_prospect_activities", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreach_prospect_activities_outreach_prospects_prospect_id",
                        column: x => x.prospect_id,
                        principalTable: "outreach_prospects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outreach_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    prospect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    assigned_to = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_tasks", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreach_tasks_outreach_prospects_prospect_id",
                        column: x => x.prospect_id,
                        principalTable: "outreach_prospects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_prospect_activities_prospect_id_created_at_utc",
                table: "outreach_prospect_activities",
                columns: new[] { "prospect_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_tasks_prospect_id_due_at_utc",
                table: "outreach_tasks",
                columns: new[] { "prospect_id", "due_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outreach_prospect_activities");

            migrationBuilder.DropTable(
                name: "outreach_tasks");

            migrationBuilder.DropColumn(
                name: "assigned_to",
                table: "outreach_prospects");

            migrationBuilder.DropColumn(
                name: "lead_score",
                table: "outreach_prospects");

            migrationBuilder.DropColumn(
                name: "next_action_at_utc",
                table: "outreach_prospects");

            migrationBuilder.DropColumn(
                name: "pipeline_stage",
                table: "outreach_prospects");

            migrationBuilder.DropColumn(
                name: "score_reason",
                table: "outreach_prospects");
        }
    }
}
