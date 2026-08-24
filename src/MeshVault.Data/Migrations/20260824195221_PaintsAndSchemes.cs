using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaintsAndSchemes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Paints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Brand = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Range = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Hex = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    Finish = table.Column<int>(type: "INTEGER", nullable: false),
                    Stock = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    AddedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Paints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaintSchemes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModelEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    OwnerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaintSchemes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaintSchemes_Models_ModelEntryId",
                        column: x => x.ModelEntryId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaintSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PaintSchemeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PaintId = table.Column<int>(type: "INTEGER", nullable: true),
                    PaintName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Hex = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    Technique = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Area = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaintSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaintSteps_PaintSchemes_PaintSchemeId",
                        column: x => x.PaintSchemeId,
                        principalTable: "PaintSchemes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaintSteps_Paints_PaintId",
                        column: x => x.PaintId,
                        principalTable: "Paints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SchemePhotos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PaintSchemeId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Caption = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    AddedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemePhotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemePhotos_PaintSchemes_PaintSchemeId",
                        column: x => x.PaintSchemeId,
                        principalTable: "PaintSchemes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Paints_OwnerId_NormalizedName",
                table: "Paints",
                columns: new[] { "OwnerId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaintSchemes_ModelEntryId",
                table: "PaintSchemes",
                column: "ModelEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_PaintSteps_PaintId",
                table: "PaintSteps",
                column: "PaintId");

            migrationBuilder.CreateIndex(
                name: "IX_PaintSteps_PaintSchemeId",
                table: "PaintSteps",
                column: "PaintSchemeId");

            migrationBuilder.CreateIndex(
                name: "IX_SchemePhotos_PaintSchemeId",
                table: "SchemePhotos",
                column: "PaintSchemeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaintSteps");

            migrationBuilder.DropTable(
                name: "SchemePhotos");

            migrationBuilder.DropTable(
                name: "Paints");

            migrationBuilder.DropTable(
                name: "PaintSchemes");
        }
    }
}
