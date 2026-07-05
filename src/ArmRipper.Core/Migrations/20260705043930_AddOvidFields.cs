using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOvidFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OvidApiResponse",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OvidFingerprint",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OvidSubmitted",
                table: "jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OvidApiResponse",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "OvidFingerprint",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "OvidSubmitted",
                table: "jobs");
        }
    }
}
