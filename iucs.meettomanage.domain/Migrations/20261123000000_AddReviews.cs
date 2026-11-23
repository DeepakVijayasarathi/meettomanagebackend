using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <summary>
    /// Adds reviews and review_requests tables for the review generation system.
    /// Platform-level tables (not per-tenant) — marketing site social proof + outbound invitations.
    /// </summary>
    public partial class AddReviews : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "internal"),
                    Rating = table.Column<int>(type: "smallint", nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false, defaultValue: ""),
                    AuthorRole = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "text", maxLength: 2000, nullable: false, defaultValue: ""),
                    Response = table.Column<string>(type: "text", maxLength: 2000, nullable: true),
                    ReviewUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsVisible = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AcademyTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "review_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ContactName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, defaultValue: ""),
                    ContactMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: ""),
                    ContactValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    Platform = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: ""),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    SentUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcademyTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DeletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_requests", x => x.Id);
                });

            // Indexes for common queries
            migrationBuilder.CreateIndex(
                name: "ix_reviews_visible_created",
                table: "reviews",
                columns: new[] { "IsVisible", "CreatedAtUtc", "Id" },
                filter: "IsDeleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_reviews_academy_created",
                table: "reviews",
                columns: new[] { "AcademyTenantId", "CreatedAtUtc", "Id" },
                filter: "IsDeleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_review_requests_pending",
                table: "review_requests",
                columns: new[] { "Status", "ExpiresAtUtc", "CreatedAtUtc", "Id" },
                filter: "IsDeleted = false AND Status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_review_requests_academy",
                table: "review_requests",
                columns: new[] { "AcademyTenantId", "CreatedAtUtc", "Id" },
                filter: "IsDeleted = false");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "review_requests");
            migrationBuilder.DropTable(name: "reviews");
        }
    }
}
