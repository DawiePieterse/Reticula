using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LayoutLinesAndNetwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    elevation_m = table.Column<double>(type: "double precision", nullable: false),
                    geometry = table.Column<LineString>(type: "geometry(LineString,4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_contours", x => x.id);
                    table.ForeignKey(
                        name: "fk_contours_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_contours_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "network_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    asset_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    geometry = table.Column<Geometry>(type: "geometry(Geometry,4326)", nullable: false),
                    voltage_kv = table.Column<double>(type: "double precision", nullable: true),
                    rating_kva = table.Column<double>(type: "double precision", nullable: true),
                    capacity_kva = table.Column<double>(type: "double precision", nullable: true),
                    fault_level_ka = table.Column<double>(type: "double precision", nullable: true),
                    missing_json = table.Column<string>(type: "jsonb", nullable: false),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_network_assets", x => x.id);
                    table.ForeignKey(
                        name: "fk_network_assets_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_network_assets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "roads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    osm_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    road_class = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    geometry = table.Column<LineString>(type: "geometry(LineString,4326)", nullable: false),
                    length_m = table.Column<double>(type: "double precision", nullable: false),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roads", x => x.id);
                    table.ForeignKey(
                        name: "fk_roads_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_roads_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_contours_geometry",
                table: "contours",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_contours_import_batch_id",
                table: "contours",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_contours_project_id",
                table: "contours",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_network_assets_geometry",
                table: "network_assets",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_network_assets_import_batch_id",
                table: "network_assets",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_network_assets_project_id_asset_type",
                table: "network_assets",
                columns: new[] { "project_id", "asset_type" });

            migrationBuilder.CreateIndex(
                name: "ix_roads_geometry",
                table: "roads",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_roads_import_batch_id",
                table: "roads",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_roads_project_id",
                table: "roads",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contours");

            migrationBuilder.DropTable(
                name: "network_assets");

            migrationBuilder.DropTable(
                name: "roads");
        }
    }
}
