using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class PinVariantsOnOrganize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VariantSetByOrganize",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VariantSetByOrganize",
                table: "Files");
        }
    }
}
