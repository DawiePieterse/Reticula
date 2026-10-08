using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LayoutStandsBuildings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_crs = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    crs_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    feature_count = table.Column<int>(type: "integer", nullable: false),
                    issues_json = table.Column<string>(type: "jsonb", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batches_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_batches_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    erf_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    zoning = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    geometry = table.Column<Polygon>(type: "geometry(Polygon,4326)", nullable: false),
                    area_m2 = table.Column<double>(type: "double precision", nullable: false),
                    attributes_json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stands", x => x.id);
                    table.ForeignKey(
                        name: "fk_stands_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stands_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "buildings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    osm_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    footprint = table.Column<Polygon>(type: "geometry(Polygon,4326)", nullable: false),
                    area_m2 = table.Column<double>(type: "double precision", nullable: false),
                    tags_json = table.Column<string>(type: "jsonb", nullable: false),
                    stand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    zoning = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    predicted_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    predicted_confidence = table.Column<double>(type: "double precision", nullable: false),
                    prediction_source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    low_confidence = table.Column<bool>(type: "boolean", nullable: false),
                    prediction_signals_json = table.Column<string>(type: "jsonb", nullable: false),
                    prediction_rules_hash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confirmed_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_buildings", x => x.id);
                    table.ForeignKey(
                        name: "fk_buildings_import_batches_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_buildings_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_buildings_stands_stand_id",
                        column: x => x.stand_id,
                        principalTable: "stands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_buildings_footprint",
                table: "buildings",
                column: "footprint")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_buildings_import_batch_id",
                table: "buildings",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_buildings_project_id_status_predicted_confidence",
                table: "buildings",
                columns: new[] { "project_id", "status", "predicted_confidence" });

            migrationBuilder.CreateIndex(
                name: "ix_buildings_stand_id",
                table: "buildings",
                column: "stand_id");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_created_by",
                table: "import_batches",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_import_batches_project_id_created_at",
                table: "import_batches",
                columns: new[] { "project_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stands_geometry",
                table: "stands",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_stands_import_batch_id",
                table: "stands",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_stands_project_id_erf_number",
                table: "stands",
                columns: new[] { "project_id", "erf_number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "buildings");

            migrationBuilder.DropTable(
                name: "stands");

            migrationBuilder.DropTable(
                name: "import_batches");
        }
    }
}
