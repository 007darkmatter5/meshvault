using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class SculptVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SculptKey",
                table: "Files",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SculptName",
                table: "Files",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariantLabel",
                table: "Files",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_ModelEntryId_SculptKey",
                table: "Files",
                columns: new[] { "ModelEntryId", "SculptKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_ModelEntryId_SculptKey",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "SculptKey",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "SculptName",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "VariantLabel",
                table: "Files");
        }
    }
}
