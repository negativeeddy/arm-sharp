using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPreferWidescreen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreferWidescreen",
                table: "config",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferWidescreen",
                table: "config");
        }
    }
}
