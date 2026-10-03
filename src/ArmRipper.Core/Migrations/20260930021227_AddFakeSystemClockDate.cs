using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFakeSystemClockDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FakeSystemClockDate",
                table: "config",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FakeSystemClockLibPath",
                table: "config",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FakeSystemClockDate",
                table: "config");

            migrationBuilder.DropColumn(
                name: "FakeSystemClockLibPath",
                table: "config");
        }
    }
}
