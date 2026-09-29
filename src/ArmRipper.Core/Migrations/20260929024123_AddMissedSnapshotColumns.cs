using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ArmRipper.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMissedSnapshotColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscVariant",
                table: "jobs",
                type: "TEXT",
                nullable: true);

            // Hand-added: 13f415e added this property to the model AND the EF model
            // snapshot, but shipped no migration for it. Because the snapshot already
            // listed the column, `dotnet ef migrations add` treats it as already
            // handled and will not generate it — hence the explicit AddColumn here.
            // ArmDbContextMigrationTests guards the whole chain against this.
            //
            // NOT NULL with a default, matching 20260610055000_AddManualWaitTime for
            // the identical column shape. The snapshot records this property without
            // IsRequired(), so scaffolding would emit a nullable column and every
            // pre-existing config row would read back NULL into a non-nullable int.
            migrationBuilder.AddColumn<int>(
                name: "ManualSelectionWaitTime",
                table: "config",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "MakeMkvInfoScanTimeoutMinutes",
                table: "config",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscVariant",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "ManualSelectionWaitTime",
                table: "config");

            migrationBuilder.DropColumn(
                name: "MakeMkvInfoScanTimeoutMinutes",
                table: "config");
        }
    }
}
