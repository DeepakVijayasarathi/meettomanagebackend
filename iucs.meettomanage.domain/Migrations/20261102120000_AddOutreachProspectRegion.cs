using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    [DbContext(typeof(MeetToManageDbContext))]
    [Migration("20261102120000_AddOutreachProspectRegion")]
    public partial class AddOutreachProspectRegion : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "region",
                table: "outreach_prospects",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            // Existing imports used City as the market. Preserve that useful grouping immediately.
            migrationBuilder.Sql("UPDATE outreach_prospects SET region = city WHERE region IS NULL AND city IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "region", table: "outreach_prospects");
        }
    }
}
