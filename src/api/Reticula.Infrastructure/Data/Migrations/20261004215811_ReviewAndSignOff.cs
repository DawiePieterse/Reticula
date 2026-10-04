using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewAndSignOff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "locked",
                table: "document_sets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "signed_off",
                table: "document_sets",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    changes_json = table.Column<string>(type: "jsonb", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_exports", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_exports_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    label = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    document_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    based_on_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sources_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    snapshot_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    signed_off_by = table.Column<Guid>(type: "uuid", nullable: false),
                    signed_off_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    registration_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    signed_off_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reproduction_json = table.Column<string>(type: "jsonb", nullable: true),
                    reproduced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reproduced = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_revisions_document_sets_document_set_id",
                        column: x => x.document_set_id,
                        principalTable: "document_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_revisions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity_type_entity_id",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_project_id_at",
                table: "audit_entries",
                columns: new[] { "project_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_project_exports_project_id",
                table: "project_exports",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_revisions_document_set_id",
                table: "revisions",
                column: "document_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_revisions_project_id_number",
                table: "revisions",
                columns: new[] { "project_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "project_exports");

            migrationBuilder.DropTable(
                name: "revisions");

            migrationBuilder.DropColumn(
                name: "locked",
                table: "document_sets");

            migrationBuilder.DropColumn(
                name: "signed_off",
                table: "document_sets");
        }
    }
}
