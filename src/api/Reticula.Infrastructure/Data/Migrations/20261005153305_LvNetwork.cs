using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LvNetwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lv_networks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rules_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    clause = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: false),
                    feeders_json = table.Column<string>(type: "jsonb", nullable: false),
                    issues_json = table.Column<string>(type: "jsonb", nullable: false),
                    error_count = table.Column<int>(type: "integer", nullable: false),
                    built_by = table.Column<Guid>(type: "uuid", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lv_networks", x => x.id);
                    table.ForeignKey(
                        name: "fk_lv_networks_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_lv_networks_users_built_by",
                        column: x => x.built_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lv_branches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    from_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    geometry = table.Column<LineString>(type: "geometry(LineString,4326)", nullable: false),
                    length_m = table.Column<double>(type: "double precision", nullable: false),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    feeder = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lv_branches", x => x.id);
                    table.ForeignKey(
                        name: "fk_lv_branches_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_lv_branches_lv_networks_network_id",
                        column: x => x.network_id,
                        principalTable: "lv_networks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lv_nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    geometry = table.Column<Point>(type: "geometry(Point,4326)", nullable: false),
                    label = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    candidate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    feeder = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    distance_m = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lv_nodes", x => x.id);
                    table.ForeignKey(
                        name: "fk_lv_nodes_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_lv_nodes_lv_networks_network_id",
                        column: x => x.network_id,
                        principalTable: "lv_networks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lv_branches_candidate_id",
                table: "lv_branches",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_lv_branches_geometry",
                table: "lv_branches",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_lv_branches_network_id_key",
                table: "lv_branches",
                columns: new[] { "network_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lv_networks_built_by",
                table: "lv_networks",
                column: "built_by");

            migrationBuilder.CreateIndex(
                name: "ix_lv_networks_project_id",
                table: "lv_networks",
                column: "project_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lv_nodes_candidate_id",
                table: "lv_nodes",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "ix_lv_nodes_geometry",
                table: "lv_nodes",
                column: "geometry")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "ix_lv_nodes_network_id_key",
                table: "lv_nodes",
                columns: new[] { "network_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lv_branches");

            migrationBuilder.DropTable(
                name: "lv_nodes");

            migrationBuilder.DropTable(
                name: "lv_networks");
        }
    }
}
