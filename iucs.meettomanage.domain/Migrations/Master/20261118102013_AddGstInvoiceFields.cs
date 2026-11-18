using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations.Master
{
    /// <inheritdoc />
    public partial class AddGstInvoiceFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_gstin",
                table: "platform_invoice",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cgst_amount",
                table: "platform_invoice",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "igst_amount",
                table: "platform_invoice",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "place_of_supply",
                table: "platform_invoice",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sac_code",
                table: "platform_invoice",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "seller_gstin",
                table: "platform_invoice",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "sgst_amount",
                table: "platform_invoice",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "buyer_gstin",
                table: "platform_invoice");

            migrationBuilder.DropColumn(
                name: "cgst_amount",
                table: "platform_invoice");

            migrationBuilder.DropColumn(
                name: "igst_amount",
                table: "platform_invoice");

            migrationBuilder.DropColumn(
                name: "place_of_supply",
                table: "platform_invoice");

            migrationBuilder.DropColumn(
                name: "sac_code",
                table: "platform_invoice");

            migrationBuilder.DropColumn(
                name: "seller_gstin",
                table: "platform_invoice");

            migrationBuilder.DropColumn(
                name: "sgst_amount",
                table: "platform_invoice");
        }
    }
}
