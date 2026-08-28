using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceAndAuditLogPaginationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_invoices_issued_at_utc",
                table: "invoices",
                column: "issued_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at_utc",
                table: "audit_logs",
                column: "created_at_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_issued_at_utc",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_audit_logs_created_at_utc",
                table: "audit_logs");
        }
    }
}
