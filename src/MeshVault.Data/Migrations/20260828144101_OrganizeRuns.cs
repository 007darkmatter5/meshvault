using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrganizeRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizeRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    RanUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UndoneUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FilesDeleted = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelsRemoved = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizeRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizeSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrganizeRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelId = table.Column<int>(type: "INTEGER", nullable: true),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    FromModelId = table.Column<int>(type: "INTEGER", nullable: true),
                    ToModelCreated = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizeSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizeSteps_OrganizeRuns_OrganizeRunId",
                        column: x => x.OrganizeRunId,
                        principalTable: "OrganizeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizeSteps_OrganizeRunId",
                table: "OrganizeSteps",
                column: "OrganizeRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizeSteps");

            migrationBuilder.DropTable(
                name: "OrganizeRuns");
        }
    }
}
