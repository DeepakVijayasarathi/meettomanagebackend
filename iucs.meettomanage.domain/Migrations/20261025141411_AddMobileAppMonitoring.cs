using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileAppMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mobile_app_installations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    installation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    app_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    build_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    runtime_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    notifications_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    biometric_lock_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_seen_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_successful_sync_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error_category = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    heartbeat_count = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mobile_app_installations", x => x.id);
                    table.ForeignKey(
                        name: "fk_mobile_app_installations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mobile_app_installations_installation_id",
                table: "mobile_app_installations",
                column: "installation_id",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_mobile_app_installations_last_seen_at_utc",
                table: "mobile_app_installations",
                column: "last_seen_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_mobile_app_installations_user_id_last_seen_at_utc",
                table: "mobile_app_installations",
                columns: new[] { "user_id", "last_seen_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mobile_app_installations");
        }
    }
}
