using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LvLoads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "boxes_json",
                table: "lv_networks",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "loads_clause",
                table: "lv_networks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "loads_summary_json",
                table: "lv_networks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phases_json",
                table: "lv_networks",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "lv_loads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: false),
                    load_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kva = table.Column<double>(type: "double precision", nullable: false),
                    branch_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    node_key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    offset_m = table.Column<double>(type: "double precision", nullable: false),
                    service_m = table.Column<double>(type: "double precision", nullable: false),
                    box = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    feeder = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    distance_m = table.Column<double>(type: "double precision", nullable: true),
                    phase = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    service = table.Column<LineString>(type: "geometry(LineString,4326)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lv_loads", x => x.id);
                    table.ForeignKey(
                        name: "fk_lv_loads_lv_networks_network_id",
                        column: x => x.network_id,
                        principalTable: "lv_networks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lv_loads_network_id_load_point_id",
                table: "lv_loads",
                columns: new[] { "network_id", "load_point_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lv_loads");

            migrationBuilder.DropColumn(
                name: "boxes_json",
                table: "lv_networks");

            migrationBuilder.DropColumn(
                name: "loads_clause",
                table: "lv_networks");

            migrationBuilder.DropColumn(
                name: "loads_summary_json",
                table: "lv_networks");

            migrationBuilder.DropColumn(
                name: "phases_json",
                table: "lv_networks");
        }
    }
}
