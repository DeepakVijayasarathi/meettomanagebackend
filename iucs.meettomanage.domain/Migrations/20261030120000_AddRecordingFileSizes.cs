using iucs.meettomanage.domain.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    [DbContext(typeof(MeetToManageDbContext))]
    [Migration("20261030120000_AddRecordingFileSizes")]
    public partial class AddRecordingFileSizes : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "file_size_bytes",
                table: "session_recordings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "file_size_bytes",
                table: "personal_room_recordings",
                type: "bigint",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "file_size_bytes", table: "session_recordings");
            migrationBuilder.DropColumn(name: "file_size_bytes", table: "personal_room_recordings");
        }
    }
}
