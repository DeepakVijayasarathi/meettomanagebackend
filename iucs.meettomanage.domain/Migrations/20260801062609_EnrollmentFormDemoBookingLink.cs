using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentFormDemoBookingLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "demo_booking_id",
                table: "enrollment_forms",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_forms_demo_booking_id",
                table: "enrollment_forms",
                column: "demo_booking_id",
                unique: true,
                filter: "\"is_deleted\" = FALSE");

            migrationBuilder.AddForeignKey(
                name: "fk_enrollment_forms_demo_bookings_demo_booking_id",
                table: "enrollment_forms",
                column: "demo_booking_id",
                principalTable: "demo_bookings",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_enrollment_forms_demo_bookings_demo_booking_id",
                table: "enrollment_forms");

            migrationBuilder.DropIndex(
                name: "ix_enrollment_forms_demo_booking_id",
                table: "enrollment_forms");

            migrationBuilder.DropColumn(
                name: "demo_booking_id",
                table: "enrollment_forms");
        }
    }
}
