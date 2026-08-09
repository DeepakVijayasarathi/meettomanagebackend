using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.readernest.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreInquiries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "store_inquiries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    parent_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    parent_phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    child_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    child_age = table.Column<int>(type: "integer", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_store_inquiries", x => x.id);
                    table.ForeignKey(
                        name: "fk_store_inquiries_package_plans_package_plan_id",
                        column: x => x.package_plan_id,
                        principalTable: "package_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_store_inquiries_package_plan_id",
                table: "store_inquiries",
                column: "package_plan_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_inquiries");
        }
    }
}
