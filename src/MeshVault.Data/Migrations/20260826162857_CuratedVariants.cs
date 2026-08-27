using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class CuratedVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VariantRank",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "VariantSetByUser",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "VariantDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    MatchTerms = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PreviewRank = table.Column<int>(type: "INTEGER", nullable: false),
                    IsFiller = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariantDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VariantDefinitions_NormalizedName",
                table: "VariantDefinitions",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VariantDefinitions");

            migrationBuilder.DropColumn(
                name: "VariantRank",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "VariantSetByUser",
                table: "Files");
        }
    }
}
