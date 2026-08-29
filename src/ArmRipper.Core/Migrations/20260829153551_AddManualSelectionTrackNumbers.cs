using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddManualSelectionTrackNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ManualSelection",
                table: "system_drives",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualSelectionTrackNumbers",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManualSelection",
                table: "config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ManualSelection",
                table: "system_drives");

            migrationBuilder.DropColumn(
                name: "ManualSelectionTrackNumbers",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "ManualSelection",
                table: "config");
        }
    }
}
