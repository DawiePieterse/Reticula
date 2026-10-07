using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Placement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "candidates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "field");

            migrationBuilder.CreateTable(
                name: "lv_placements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rules_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    clause = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: false),
                    transformers = table.Column<int>(type: "integer", nullable: false),
                    loads = table.Column<int>(type: "integer", nullable: false),
                    unplaced = table.Column<int>(type: "integer", nullable: false),
                    built_by = table.Column<Guid>(type: "uuid", nullable: false),
                    built_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lv_placements", x => x.id);
                    table.ForeignKey(
                        name: "fk_lv_placements_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_lv_placements_users_built_by",
                        column: x => x.built_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lv_placements_built_by",
                table: "lv_placements",
                column: "built_by");

            migrationBuilder.CreateIndex(
                name: "ix_lv_placements_project_id",
                table: "lv_placements",
                column: "project_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lv_placements");

            migrationBuilder.DropColumn(
                name: "source",
                table: "candidates");
        }
    }
}
