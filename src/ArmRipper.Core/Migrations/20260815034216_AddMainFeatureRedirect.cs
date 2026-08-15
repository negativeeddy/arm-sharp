using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMainFeatureRedirect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MainFeatureOverrideTrackNumber",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MainFeatureTrackNumber",
                table: "disc_metadata",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MainFeatureOverrideTrackNumber",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "MainFeatureTrackNumber",
                table: "disc_metadata");
        }
    }
}
