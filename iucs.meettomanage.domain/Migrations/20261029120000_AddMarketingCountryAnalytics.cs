using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    [DbContext(typeof(MeetToManageDbContext))]
    [Migration("20261029120000_AddMarketingCountryAnalytics")]
    public partial class AddMarketingCountryAnalytics : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country",
                table: "marketing_events",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country",
                table: "marketing_events");
        }
    }
}
