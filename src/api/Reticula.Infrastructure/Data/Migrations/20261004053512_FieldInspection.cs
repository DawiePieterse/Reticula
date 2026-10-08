using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FieldInspection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Polygon>(
                name: "footprint",
                table: "buildings",
                type: "geometry(Polygon,4326)",
                nullable: true,
                oldClrType: typeof(Polygon),
                oldType: "geometry(Polygon,4326)");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inspected_at",
                table: "buildings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "inspected_by",
                table: "buildings",
                type: "uuid",
                nullable: true);

            // Existing buildings get their footprint centroid; only then can the column be required.
            migrationBuilder.AddColumn<Point>(
                name: "location",
                table: "buildings",
                type: "geometry(Point,4326)",
                nullable: true);

            migrationBuilder.Sql("UPDATE buildings SET location = ST_Centroid(footprint) WHERE location IS NULL;");

            migrationBuilder.AlterColumn<Point>(
                name: "location",
                table: "buildings",
                type: "geometry(Point,4326)",
                nullable: false,
                oldClrType: typeof(Point),
                oldType: "geometry(Point,4326)",
                oldNullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "buildings",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.CreateTable(
                name: "assumptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cleared_by = table.Column<Guid>(type: "uuid", nullable: true),
                    cleared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    clear_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assumptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_assumptions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    geometry = table.Column<Geometry>(type: "geometry(Geometry,4326)", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_candidates", x => x.id);
                    table.ForeignKey(
                        name: "fk_candidates_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_candidates_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "load_points",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    special_load = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    observations_json = table.Column<string>(type: "jsonb", nullable: false),
                    income_band = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    estimated_kva = table.Column<double>(type: "double precision", nullable: false),
                    kva = table.Column<double>(type: "double precision", nullable: false),
                    overridden = table.Column<bool>(type: "boolean", nullable: false),
                    override_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    missing_json = table.Column<string>(type: "jsonb", nullable: false),
                    trace_json = table.Column<string>(type: "jsonb", nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_load_points", x => x.id);
                    table.ForeignKey(
                        name: "fk_load_points_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_load_points_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "inspections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    value = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    position = table.Column<Point>(type: "geometry(Point,4326)", nullable: true),
                    accuracy_m = table.Column<double>(type: "double precision", nullable: true),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    inspector_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inspections", x => x.id);
                    table.ForeignKey(
                        name: "fk_inspections_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_inspections_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_inspections_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_inspections_users_inspector_id",
                        column: x => x.inspector_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "photos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    inspection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_photos", x => x.id);
                    table.ForeignKey(
                        name: "fk_photos_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_photos_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_photos_inspections_inspection_id",
                        column: x => x.inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_photos_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_photos_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_buildings_location",
                table: "buildings",
                column: "location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_assumptions_project_id_status",
                table: "assumptions",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_assumptions_project_id_subject_type_subject_id_code",
                table: "assumptions",
                columns: new[] { "project_id", "subject_type", "subject_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_candidates_created_by",
                table: "candidates",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_candidates_geometry",
                table: "candidates",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_candidates_project_id_kind",
                table: "candidates",
                columns: new[] { "project_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_inspections_building_id",
                table: "inspections",
                column: "building_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspections_candidate_id",
                table: "inspections",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspections_inspector_id",
                table: "inspections",
                column: "inspector_id");

            migrationBuilder.CreateIndex(
                name: "ix_inspections_project_id_captured_at",
                table: "inspections",
                columns: new[] { "project_id", "captured_at" });

            migrationBuilder.CreateIndex(
                name: "ix_load_points_building_id",
                table: "load_points",
                column: "building_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_load_points_project_id",
                table: "load_points",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_photos_building_id",
                table: "photos",
                column: "building_id");

            migrationBuilder.CreateIndex(
                name: "ix_photos_candidate_id",
                table: "photos",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_photos_inspection_id",
                table: "photos",
                column: "inspection_id");

            migrationBuilder.CreateIndex(
                name: "ix_photos_project_id",
                table: "photos",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_photos_uploaded_by",
                table: "photos",
                column: "uploaded_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assumptions");

            migrationBuilder.DropTable(
                name: "load_points");

            migrationBuilder.DropTable(
                name: "photos");

            migrationBuilder.DropTable(
                name: "inspections");

            migrationBuilder.DropTable(
                name: "candidates");

            migrationBuilder.DropIndex(
                name: "ix_buildings_location",
                table: "buildings");

            migrationBuilder.DropColumn(
                name: "inspected_at",
                table: "buildings");

            migrationBuilder.DropColumn(
                name: "inspected_by",
                table: "buildings");

            migrationBuilder.DropColumn(
                name: "location",
                table: "buildings");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "buildings");

            migrationBuilder.AlterColumn<Polygon>(
                name: "footprint",
                table: "buildings",
                type: "geometry(Polygon,4326)",
                nullable: false,
                oldClrType: typeof(Polygon),
                oldType: "geometry(Polygon,4326)",
                oldNullable: true);
        }
    }
}
