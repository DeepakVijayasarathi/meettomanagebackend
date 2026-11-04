using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddHomeworkTestsAndNotificationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                table: "notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_type",
                table: "notifications",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teacher_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    attachment_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    attachment_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    due_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    max_marks = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    topic = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    results_released_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_assignments_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignments_teacher_profiles_teacher_profile_id",
                        column: x => x.teacher_profile_id,
                        principalTable: "teacher_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assignment_submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    answer_text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    attachment_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    attachment_file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    submitted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    marks = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    feedback = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    graded_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_submissions", x => x.id);
                    table.ForeignKey(
                        name: "fk_assignment_submissions_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_assignment_submissions_children_child_id",
                        column: x => x.child_id,
                        principalTable: "children",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_submissions_assignment_id_child_id",
                table: "assignment_submissions",
                columns: new[] { "assignment_id", "child_id" },
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_assignment_submissions_child_id_status",
                table: "assignment_submissions",
                columns: new[] { "child_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_batch_id_due_at_utc",
                table: "assignments",
                columns: new[] { "batch_id", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_assignments_teacher_profile_id_created_at_utc",
                table: "assignments",
                columns: new[] { "teacher_profile_id", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignment_submissions");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "target_type",
                table: "notifications");
        }
    }
}
