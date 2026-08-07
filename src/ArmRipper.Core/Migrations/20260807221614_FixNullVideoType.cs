using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class FixNullVideoType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill NULL/empty VideoType so the NOT NULL constraint can be applied.
            migrationBuilder.Sql("UPDATE jobs SET VideoType = 'unknown' WHERE VideoType IS NULL OR VideoType = ''");

            migrationBuilder.AlterColumn<string>(
                name: "VideoType",
                table: "jobs",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "unknown",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscNumber",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscNumberAuto",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscNumberManual",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumberAuto",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumberManual",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StartingEpisodeNumber",
                table: "jobs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscNumber",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "DiscNumberAuto",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "DiscNumberManual",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "SeasonNumberAuto",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "SeasonNumberManual",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "StartingEpisodeNumber",
                table: "jobs");

            migrationBuilder.AlterColumn<string>(
                name: "VideoType",
                table: "jobs",
                type: "TEXT",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 20);
        }
    }
}
