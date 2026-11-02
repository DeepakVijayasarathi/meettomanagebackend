using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddColdOutreach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outreach_campaigns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_campaigns", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outreach_prospects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    first_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    academy_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    city = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    source = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unsubscribe_token = table.Column<Guid>(type: "uuid", nullable: false),
                    last_contacted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    unsubscribed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_prospects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outreach_sends",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prospect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sent_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_sends", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outreach_campaign_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    delay_days = table.Column<int>(type: "integer", nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_campaign_steps", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreach_campaign_steps_outreach_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "outreach_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outreach_campaign_recipients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    prospect_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_step = table.Column<int>(type: "integer", nullable: false),
                    next_send_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outreach_campaign_recipients", x => x.id);
                    table.ForeignKey(
                        name: "fk_outreach_campaign_recipients_outreach_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalTable: "outreach_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_outreach_campaign_recipients_outreach_prospects_prospect_id",
                        column: x => x.prospect_id,
                        principalTable: "outreach_prospects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_campaign_recipients_campaign_id_prospect_id",
                table: "outreach_campaign_recipients",
                columns: new[] { "campaign_id", "prospect_id" },
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_campaign_recipients_prospect_id",
                table: "outreach_campaign_recipients",
                column: "prospect_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_campaign_recipients_status_next_send_at_utc",
                table: "outreach_campaign_recipients",
                columns: new[] { "status", "next_send_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_outreach_campaign_steps_campaign_id_order",
                table: "outreach_campaign_steps",
                columns: new[] { "campaign_id", "order" },
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_prospects_email",
                table: "outreach_prospects",
                column: "email",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_prospects_status",
                table: "outreach_prospects",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_prospects_unsubscribe_token",
                table: "outreach_prospects",
                column: "unsubscribe_token",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_sends_campaign_id",
                table: "outreach_sends",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "ix_outreach_sends_sent_at_utc",
                table: "outreach_sends",
                column: "sent_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outreach_campaign_recipients");

            migrationBuilder.DropTable(
                name: "outreach_campaign_steps");

            migrationBuilder.DropTable(
                name: "outreach_sends");

            migrationBuilder.DropTable(
                name: "outreach_prospects");

            migrationBuilder.DropTable(
                name: "outreach_campaigns");
        }
    }
}
