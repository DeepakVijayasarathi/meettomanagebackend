using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace iucs.meettomanage.domain.Migrations
{
    /// <inheritdoc />
    public partial class AddTwoFactorSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "two_factor_challenge_expires_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "two_factor_challenge_hash",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "two_factor_enabled_at_utc",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "two_factor_last_used_step",
                table: "users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "two_factor_recovery_code_hashes",
                table: "users",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "two_factor_secret_protected",
                table: "users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_two_factor_challenge_hash",
                table: "users",
                column: "two_factor_challenge_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_two_factor_challenge_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_factor_challenge_expires_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_factor_challenge_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_factor_enabled_at_utc",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_factor_last_used_step",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_factor_recovery_code_hashes",
                table: "users");

            migrationBuilder.DropColumn(
                name: "two_factor_secret_protected",
                table: "users");
        }
    }
}
