using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LvDesignRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "phases",
                table: "load_points",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "design_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    rules_ref = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    input_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    passed = table.Column<bool>(type: "boolean", nullable: true),
                    summary_json = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_design_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_design_runs_project_id_kind_created_at",
                table: "design_runs",
                columns: new[] { "project_id", "kind", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "design_runs");

            migrationBuilder.DropColumn(
                name: "phases",
                table: "load_points");
        }
    }
}
