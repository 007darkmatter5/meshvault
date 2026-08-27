using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class VariantGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupKey",
                table: "Models",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "Models",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GroupPrimary",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Models_LibraryId_GroupKey",
                table: "Models",
                columns: new[] { "LibraryId", "GroupKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Models_LibraryId_GroupKey",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "GroupKey",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "GroupPrimary",
                table: "Models");
        }
    }
}
