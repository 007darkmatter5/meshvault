using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class LibraryNamingConvention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileTemplate",
                table: "Libraries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FolderTemplate",
                table: "Libraries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RenameFiles",
                table: "Libraries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileTemplate",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "FolderTemplate",
                table: "Libraries");

            migrationBuilder.DropColumn(
                name: "RenameFiles",
                table: "Libraries");
        }
    }
}
