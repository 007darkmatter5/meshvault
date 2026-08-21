using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <inheritdoc />
    public partial class DesignersCollectionsAndFavorites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old "Designer" text column and "IsFavorite" flag are dropped at
            // the end of this migration, after their contents have been copied
            // into the new Designers and Favorites tables.
            migrationBuilder.AddColumn<int>(
                name: "DesignerId",
                table: "Models",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceSite",
                table: "Models",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Collections",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Collections",
                type: "TEXT",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Designers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Designers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Favorites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModelEntryId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Favorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Favorites_Models_ModelEntryId",
                        column: x => x.ModelEntryId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Models_DesignerId",
                table: "Models",
                column: "DesignerId");

            migrationBuilder.CreateIndex(
                name: "IX_Models_SourceSite",
                table: "Models",
                column: "SourceSite");

            // Backfilled before the unique index is created, otherwise several
            // pre-existing collections would collide on the empty defaults.
            migrationBuilder.Sql(
                "UPDATE Collections SET OwnerId = 'local' WHERE OwnerId = '' OR OwnerId IS NULL;");
            migrationBuilder.Sql(
                "UPDATE Collections SET NormalizedName = LOWER(TRIM(Name)) WHERE NormalizedName = '';");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_OwnerId_NormalizedName",
                table: "Collections",
                columns: new[] { "OwnerId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Designers_NormalizedName",
                table: "Designers",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Favorites_ModelEntryId",
                table: "Favorites",
                column: "ModelEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Favorites_UserId_ModelEntryId",
                table: "Favorites",
                columns: new[] { "UserId", "ModelEntryId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Designers_DesignerId",
                table: "Models",
                column: "DesignerId",
                principalTable: "Designers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // --- Carry existing data across before dropping the old columns ---

            // Every distinct designer name becomes a Designer row.
            migrationBuilder.Sql("""
                INSERT INTO Designers (Name, NormalizedName, CreatedUtc)
                SELECT DISTINCT TRIM(Designer), LOWER(TRIM(Designer)), CURRENT_TIMESTAMP
                FROM Models
                WHERE Designer IS NOT NULL AND TRIM(Designer) <> '';
                """);

            migrationBuilder.Sql("""
                UPDATE Models
                SET DesignerId = (
                    SELECT d.Id FROM Designers d
                    WHERE d.NormalizedName = LOWER(TRIM(Models.Designer))
                )
                WHERE Designer IS NOT NULL AND TRIM(Designer) <> '';
                """);

            // Existing stars become favorites owned by the local user, which the
            // first real account inherits when authentication is added.
            migrationBuilder.Sql("""
                INSERT INTO Favorites (ModelEntryId, UserId, CreatedUtc)
                SELECT Id, 'local', CURRENT_TIMESTAMP FROM Models WHERE IsFavorite = 1;
                """);

            // Backfill the source site for links already stored.
            foreach (var (host, name) in new[]
            {
                ("makerworld.com", "MakerWorld"), ("printables.com", "Printables"),
                ("thingiverse.com", "Thingiverse"), ("thangs.com", "Thangs"),
                ("cults3d.com", "Cults3D"), ("myminifactory.com", "MyMiniFactory"),
                ("patreon.com", "Patreon"), ("gumroad.com", "Gumroad"),
                ("etsy.com", "Etsy"), ("github.com", "GitHub"),
            })
            {
                migrationBuilder.Sql(
                    $"UPDATE Models SET SourceSite = '{name}' " +
                    $"WHERE SourceUrl LIKE '%{host}%' AND SourceSite IS NULL;");
            }

            migrationBuilder.Sql(
                "UPDATE Models SET SourceSite = 'Other' WHERE SourceUrl IS NOT NULL AND SourceSite IS NULL;");

            migrationBuilder.DropColumn(name: "Designer", table: "Models");
            migrationBuilder.DropColumn(name: "IsFavorite", table: "Models");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Models_Designers_DesignerId",
                table: "Models");

            migrationBuilder.DropTable(
                name: "Designers");

            migrationBuilder.DropTable(
                name: "Favorites");

            migrationBuilder.DropIndex(
                name: "IX_Models_DesignerId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_Models_SourceSite",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_Collections_OwnerId_NormalizedName",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "DesignerId",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "SourceSite",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Collections");

            migrationBuilder.AddColumn<string>(
                name: "Designer",
                table: "Models",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFavorite",
                table: "Models",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
