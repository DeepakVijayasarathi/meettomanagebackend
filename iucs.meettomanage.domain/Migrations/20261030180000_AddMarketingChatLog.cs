using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketingChatLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "marketing_chat_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    visitor_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    medium = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    campaign = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    landing_path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    first_message = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    message_count = table.Column<int>(type: "integer", nullable: false),
                    last_message_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    booked_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_chat_conversations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "marketing_chat_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_marketing_chat_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_marketing_chat_messages_marketing_chat_conversations_conver",
                        column: x => x.conversation_id,
                        principalTable: "marketing_chat_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_marketing_chat_conversations_conversation_key",
                table: "marketing_chat_conversations",
                column: "conversation_key",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_marketing_chat_conversations_last_message_at_utc",
                table: "marketing_chat_conversations",
                column: "last_message_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_marketing_chat_messages_conversation_id_position",
                table: "marketing_chat_messages",
                columns: new[] { "conversation_id", "position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "marketing_chat_messages");

            migrationBuilder.DropTable(
                name: "marketing_chat_conversations");
        }
    }
}
