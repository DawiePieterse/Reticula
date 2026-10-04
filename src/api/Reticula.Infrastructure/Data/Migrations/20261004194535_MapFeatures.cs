using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MapFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "map_features",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    layer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    subtype = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    elevation_m = table.Column<double>(type: "double precision", nullable: true),
                    geometry = table.Column<Geometry>(type: "geometry(Geometry,4326)", nullable: false),
                    length_m = table.Column<double>(type: "double precision", nullable: false),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_features", x => x.id);
                    table.ForeignKey(
                        name: "fk_map_features_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_features_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_map_features_geometry",
                table: "map_features",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_map_features_import_batch_id",
                table: "map_features",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_map_features_project_id_layer",
                table: "map_features",
                columns: new[] { "project_id", "layer" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "map_features");
        }
    }
}
