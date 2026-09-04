using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <summary>
    /// Collections stop being owned by an account and become part of the
    /// library, and a model gains a star saying which of them names its folder.
    /// </summary>
    /// <remarks>
    /// The merge below is hand-written and runs before the column goes, because
    /// two accounts were each allowed a collection called "To Print" and the
    /// new unique index would refuse them. Losing one outright would take its
    /// models with it, so the memberships are unioned onto the survivor first.
    ///
    /// <b>Not reversible.</b> Down puts the column and the old index back, but
    /// it cannot know which owner each merged collection came from, so what was
    /// two collections stays one.
    /// </remarks>
    public partial class SharedCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A blank normalised name would merge with every other blank, so
            // fill it from the name before anything is compared.
            migrationBuilder.Sql(@"
                UPDATE Collections SET NormalizedName = lower(Name)
                WHERE NormalizedName IS NULL OR trim(NormalizedName) = '';");

            // Keep a description rather than the survivor's emptiness: of two
            // collections called ""Terrain"", the one somebody bothered to
            // describe is the one worth keeping the words from.
            migrationBuilder.Sql(@"
                UPDATE Collections
                SET Description = (
                    SELECT d.Description FROM Collections d
                    WHERE d.NormalizedName = Collections.NormalizedName
                      AND d.Description IS NOT NULL AND trim(d.Description) <> ''
                    ORDER BY d.Id LIMIT 1)
                WHERE (Description IS NULL OR trim(Description) = '')
                  AND Id IN (SELECT MIN(Id) FROM Collections GROUP BY NormalizedName);");

            // Every model any duplicate held now belongs to the survivor. OR
            // IGNORE because both may already hold the same model, and the pair
            // is the primary key.
            migrationBuilder.Sql(@"
                INSERT OR IGNORE INTO CollectionModelEntry (CollectionsId, ModelsId)
                SELECT keeper.Id, j.ModelsId
                FROM CollectionModelEntry j
                JOIN Collections c ON c.Id = j.CollectionsId
                JOIN (SELECT NormalizedName, MIN(Id) AS Id
                      FROM Collections GROUP BY NormalizedName) keeper
                  ON keeper.NormalizedName = c.NormalizedName
                WHERE j.CollectionsId <> keeper.Id;");

            migrationBuilder.Sql(@"
                DELETE FROM CollectionModelEntry
                WHERE CollectionsId NOT IN (
                    SELECT MIN(Id) FROM Collections GROUP BY NormalizedName);");

            migrationBuilder.Sql(@"
                DELETE FROM Collections
                WHERE Id NOT IN (SELECT MIN(Id) FROM Collections GROUP BY NormalizedName);");

            // Native ALTER TABLE ADD COLUMN on SQLite, so no table rebuild is
            // queued and the SQL below still sees a settled database. Dropping
            // OwnerId does queue one, which is why it comes last.
            migrationBuilder.AddColumn<int>(
                name: "PrimaryCollectionId",
                table: "Models",
                type: "INTEGER",
                nullable: true);

            // Backfilling the stars is a whole migration of its own
            // (StarTheFilingCollection) rather than a few more lines here. The
            // SQLite provider rebuilds a table both to drop a column and to add
            // one carrying a foreign key, and raw SQL issued once a rebuild is
            // pending reads a database midway through being rearranged. EF
            // warns about exactly that, and the fix it suggests is the one
            // taken: a subsequent migration starts with nothing pending.
            migrationBuilder.DropIndex(
                name: "IX_Collections_OwnerId_NormalizedName",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Collections");

            migrationBuilder.CreateIndex(
                name: "IX_Models_PrimaryCollectionId",
                table: "Models",
                column: "PrimaryCollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_NormalizedName",
                table: "Collections",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Models_Collections_PrimaryCollectionId",
                table: "Models",
                column: "PrimaryCollectionId",
                principalTable: "Collections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Models_Collections_PrimaryCollectionId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_Models_PrimaryCollectionId",
                table: "Models");

            migrationBuilder.DropIndex(
                name: "IX_Collections_NormalizedName",
                table: "Collections");

            migrationBuilder.DropColumn(
                name: "PrimaryCollectionId",
                table: "Models");

            // Everything lands on one owner, because which account each merged
            // collection belonged to is not recorded anywhere by then.
            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Collections",
                type: "TEXT",
                maxLength: 450,
                nullable: false,
                defaultValue: "local");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_OwnerId_NormalizedName",
                table: "Collections",
                columns: new[] { "OwnerId", "NormalizedName" },
                unique: true);
        }
    }
}
