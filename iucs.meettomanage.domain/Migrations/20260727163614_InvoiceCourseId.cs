using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceCourseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "course_id",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_course_id",
                table: "invoices",
                column: "course_id");

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_courses_course_id",
                table: "invoices",
                column: "course_id",
                principalTable: "courses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invoices_courses_course_id",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoices_course_id",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "course_id",
                table: "invoices");
        }
    }
}
