using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletedStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MakeMkvProgress",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "ProgressMessage",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "TranscodeProgress",
                table: "jobs");

            migrationBuilder.AddColumn<string>(
                name: "CompletedStages",
                table: "jobs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedStages",
                table: "jobs");

            migrationBuilder.AddColumn<int>(
                name: "MakeMkvProgress",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProgressMessage",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TranscodeProgress",
                table: "jobs",
                type: "INTEGER",
                nullable: true);
        }
    }
}
