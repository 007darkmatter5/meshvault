using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaintBrandsAndRanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaintBrands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaintBrands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaintRanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PaintBrandId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaintRanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaintRanges_PaintBrands_PaintBrandId",
                        column: x => x.PaintBrandId,
                        principalTable: "PaintBrands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaintBrands_NormalizedName",
                table: "PaintBrands",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaintRanges_PaintBrandId_NormalizedName",
                table: "PaintRanges",
                columns: new[] { "PaintBrandId", "NormalizedName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaintRanges");

            migrationBuilder.DropTable(
                name: "PaintBrands");
        }
    }
}
