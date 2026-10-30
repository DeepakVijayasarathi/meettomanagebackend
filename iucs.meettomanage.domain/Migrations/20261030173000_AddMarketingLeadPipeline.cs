using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations;

[DbContext(typeof(MeetToManageDbContext))]
[Migration("20261030173000_AddMarketingLeadPipeline")]
public partial class AddMarketingLeadPipeline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        AddText(migrationBuilder, "assigned_to", 150); AddText(migrationBuilder, "campaign", 100);
        AddText(migrationBuilder, "internal_notes", 1000); AddText(migrationBuilder, "landing_path", 200);
        AddText(migrationBuilder, "medium", 100); AddText(migrationBuilder, "source", 100); AddText(migrationBuilder, "visitor_id", 100);
        migrationBuilder.AddColumn<DateTime>("last_contacted_at_utc", "platform_demo_requests", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<DateTime>("next_follow_up_at_utc", "platform_demo_requests", "timestamp with time zone", nullable: true);
        migrationBuilder.CreateIndex("ix_platform_demo_requests_next_follow_up_at_utc", "platform_demo_requests", "next_follow_up_at_utc");
    }

    private static void AddText(MigrationBuilder migrationBuilder, string name, int length) =>
        migrationBuilder.AddColumn<string>(name, "platform_demo_requests", $"character varying({length})", maxLength: length, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("ix_platform_demo_requests_next_follow_up_at_utc", "platform_demo_requests");
        foreach (var name in new[] { "assigned_to", "campaign", "internal_notes", "landing_path", "medium", "source", "visitor_id", "last_contacted_at_utc", "next_follow_up_at_utc" })
            migrationBuilder.DropColumn(name, "platform_demo_requests");
    }
}
