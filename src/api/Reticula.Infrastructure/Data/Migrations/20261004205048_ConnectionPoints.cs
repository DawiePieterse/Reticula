using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConnectionPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_points",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<Point>(type: "geometry(Point,4326)", nullable: false),
                    voltage_kv = table.Column<double>(type: "double precision", nullable: false),
                    available_capacity_kva = table.Column<double>(type: "double precision", nullable: true),
                    fault_mva_max = table.Column<double>(type: "double precision", nullable: true),
                    fault_mva_min = table.Column<double>(type: "double precision", nullable: true),
                    x_r = table.Column<double>(type: "double precision", nullable: true),
                    sending_voltage_pct = table.Column<double>(type: "double precision", nullable: true),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_points", x => x.project_id);
                    table.ForeignKey(
                        name: "fk_connection_points_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_points");
        }
    }
}
