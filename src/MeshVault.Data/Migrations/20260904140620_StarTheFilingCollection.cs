using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshVault.Data.Migrations
{
    /// <summary>
    /// Records, as a star, whichever collection was already naming each model's
    /// folder.
    /// </summary>
    /// <remarks>
    /// A migration to itself rather than the last few lines of
    /// <c>SharedCollections</c>. That one drops a column and adds one carrying a
    /// foreign key, and the SQLite provider rebuilds a table for either — so raw
    /// SQL after it reads a database midway through being rearranged, which EF
    /// warns about and advises moving to a subsequent migration. This is that
    /// migration, and it begins with nothing pending.
    ///
    /// Purely data: the schema is settled by the time it runs, so there is
    /// nothing for the model snapshot to say and Down has nothing to undo.
    /// </remarks>
    public partial class StarTheFilingCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // {collection} used to resolve to the first collection
            // alphabetically. Left unstarred, a model in several of them would
            // file without the level at all -- so the first organize after this
            // upgrade would want to move folders nobody asked it to touch.
            // Writing the old choice down keeps the library still and makes the
            // choice visible and changeable, which is the point of the star.
            //
            // Only where there is a choice to record: a model in one collection
            // is filed under it implicitly and stores no star.
            migrationBuilder.Sql(@"
                UPDATE Models
                SET PrimaryCollectionId = (
                    SELECT c.Id FROM Collections c
                    JOIN CollectionModelEntry j ON j.CollectionsId = c.Id
                    WHERE j.ModelsId = Models.Id
                    ORDER BY c.Name LIMIT 1)
                WHERE (SELECT COUNT(*) FROM CollectionModelEntry j
                       WHERE j.ModelsId = Models.Id) > 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Clearing the stars would be worse than leaving them: the column
            // goes with SharedCollections' Down anyway, and until then they are
            // the only record of how the library is filed.
        }
    }
}
