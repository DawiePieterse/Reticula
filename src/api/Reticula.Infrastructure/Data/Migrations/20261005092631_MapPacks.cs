using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MapPacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "map_packs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    min_lon = table.Column<double>(type: "double precision", nullable: false),
                    min_lat = table.Column<double>(type: "double precision", nullable: false),
                    max_lon = table.Column<double>(type: "double precision", nullable: false),
                    max_lat = table.Column<double>(type: "double precision", nullable: false),
                    max_zoom = table.Column<int>(type: "integer", nullable: false),
                    tile_count = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    built_by = table.Column<Guid>(type: "uuid", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_map_packs", x => x.id);
                    table.ForeignKey(
                        name: "fk_map_packs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_map_packs_users_built_by",
                        column: x => x.built_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_map_packs_built_by",
                table: "map_packs",
                column: "built_by");

            migrationBuilder.CreateIndex(
                name: "ix_map_packs_project_id",
                table: "map_packs",
                column: "project_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "map_packs");
        }
    }
}
