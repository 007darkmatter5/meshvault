using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotCameraView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SnapshotViewX",
                table: "Models",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SnapshotViewY",
                table: "Models",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SnapshotViewZ",
                table: "Models",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SnapshotViewX",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SnapshotViewY",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SnapshotViewZ",
                table: "Models");
        }
    }
}
