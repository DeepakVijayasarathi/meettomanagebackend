using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCrm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "city",
                table: "platform_demo_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expected_close_at_utc",
                table: "platform_demo_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "expected_monthly_value",
                table: "platform_demo_requests",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "expected_plan",
                table: "platform_demo_requests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "region",
                table: "platform_demo_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "segment",
                table: "platform_demo_requests",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "student_count",
                table: "platform_demo_requests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website",
                table: "platform_demo_requests",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "crm_customer_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    author = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_customer_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "crm_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_tasks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "crm_team_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_crm_team_members", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crm_customer_notes_tenant_id",
                table: "crm_customer_notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_assigned_to_user_id_completed_at_utc_due_at_utc",
                table: "crm_tasks",
                columns: new[] { "assigned_to_user_id", "completed_at_utc", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_lead_id",
                table: "crm_tasks",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_tasks_tenant_id",
                table: "crm_tasks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_crm_team_members_user_id",
                table: "crm_team_members",
                column: "user_id",
                unique: true,
                filter: "\"is_deleted\" = FALSE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crm_customer_notes");

            migrationBuilder.DropTable(
                name: "crm_tasks");

            migrationBuilder.DropTable(
                name: "crm_team_members");

            migrationBuilder.DropColumn(
                name: "city",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "expected_close_at_utc",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "expected_monthly_value",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "expected_plan",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "region",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "segment",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "student_count",
                table: "platform_demo_requests");

            migrationBuilder.DropColumn(
                name: "website",
                table: "platform_demo_requests");
        }
    }
}
