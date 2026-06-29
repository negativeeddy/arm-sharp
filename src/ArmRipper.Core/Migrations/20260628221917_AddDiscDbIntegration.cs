using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscDbIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentType",
                table: "tracks",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscDbItemSlug",
                table: "tracks",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EpisodeTitle",
                table: "tracks",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackSeasonNumber",
                table: "tracks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscDbHash",
                table: "jobs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeriesTmdbId",
                table: "jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscDbApiBaseUrl",
                table: "config",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DiscDbEnabled",
                table: "config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "DiscDbMinConfidence",
                table: "config",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<bool>(
                name: "DiscDbRequireConfirmation",
                table: "config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "discdb_mappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MediaSlug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MediaTitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    MediaYear = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    TrackMappingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_discdb_mappings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_discdb_mappings_ContentHash",
                table: "discdb_mappings",
                column: "ContentHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discdb_mappings");

            migrationBuilder.DropColumn(
                name: "ContentType",
                table: "tracks");

            migrationBuilder.DropColumn(
                name: "DiscDbItemSlug",
                table: "tracks");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "tracks");

            migrationBuilder.DropColumn(
                name: "EpisodeTitle",
                table: "tracks");

            migrationBuilder.DropColumn(
                name: "TrackSeasonNumber",
                table: "tracks");

            migrationBuilder.DropColumn(
                name: "DiscDbHash",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "SeriesTmdbId",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "DiscDbApiBaseUrl",
                table: "config");

            migrationBuilder.DropColumn(
                name: "DiscDbEnabled",
                table: "config");

            migrationBuilder.DropColumn(
                name: "DiscDbMinConfidence",
                table: "config");

            migrationBuilder.DropColumn(
                name: "DiscDbRequireConfirmation",
                table: "config");
        }
    }
}
