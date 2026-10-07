using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DesignRunsDocumentsReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    messages_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_conversations_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assistant_conversations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    before_json = table.Column<string>(type: "jsonb", nullable: true),
                    after_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "connection_points",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location = table.Column<Point>(type: "geometry(Point,4326)", nullable: false),
                    voltage_kv = table.Column<double>(type: "double precision", nullable: true),
                    capacity_kva = table.Column<double>(type: "double precision", nullable: true),
                    fault3ph_ka = table.Column<double>(type: "double precision", nullable: true),
                    fault3ph_min_ka = table.Column<double>(type: "double precision", nullable: true),
                    fault1ph_ka = table.Column<double>(type: "double precision", nullable: true),
                    x_over_r = table.Column<double>(type: "double precision", nullable: true),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    received_on = table.Column<DateOnly>(type: "date", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
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
                    table.ForeignKey(
                        name: "fk_connection_points_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "design_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rules_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    inputs_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    inputs_parts_json = table.Column<string>(type: "jsonb", nullable: false),
                    request_json = table.Column<string>(type: "jsonb", nullable: false),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    result_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    construction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    fit_to_submit = table.Column<bool>(type: "boolean", nullable: false),
                    checks = table.Column<int>(type: "integer", nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    capex = table.Column<double>(type: "double precision", nullable: true),
                    lifetime = table.Column<double>(type: "double precision", nullable: true),
                    summary_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_design_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_design_runs_design_runs_parent_run_id",
                        column: x => x.parent_run_id,
                        principalTable: "design_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_design_runs_job_runs_job_id",
                        column: x => x.job_id,
                        principalTable: "job_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_design_runs_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_design_runs_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rate_libraries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rate_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    indicative = table.Column<bool>(type: "boolean", nullable: false),
                    items_json = table.Column<string>(type: "jsonb", nullable: false),
                    assemblies_json = table.Column<string>(type: "jsonb", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_libraries", x => x.id);
                    table.ForeignKey(
                        name: "fk_rate_libraries_users_imported_by",
                        column: x => x.imported_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rate_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rate = table.Column<double>(type: "double precision", nullable: false),
                    rate_date = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rate_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_rate_overrides_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_sections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_report_sections_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    explanation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assistant_drafts", x => x.id);
                    table.ForeignKey(
                        name: "fk_assistant_drafts_assistant_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "assistant_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_assistant_drafts_projects_project_id",
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
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    design_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rules_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    inputs_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fit_to_submit = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    signed_off_by = table.Column<Guid>(type: "uuid", nullable: true),
                    signed_off_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    engineer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    registration_no = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    sign_off_statement = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    reproduced = table.Column<bool>(type: "boolean", nullable: true),
                    reproduced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_revisions_design_runs_design_run_id",
                        column: x => x.design_run_id,
                        principalTable: "design_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_revisions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_revisions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_revisions_users_signed_off_by",
                        column: x => x.signed_off_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    design_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rules_ref = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rules_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    rate_date = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    design_date = table.Column<DateOnly>(type: "date", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    inputs_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked = table.Column<bool>(type: "boolean", nullable: false),
                    superseded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_documents_design_runs_design_run_id",
                        column: x => x.design_run_id,
                        principalTable: "design_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_documents_revisions_revision_id",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_conversations_project_id_user_id_updated_at",
                table: "assistant_conversations",
                columns: new[] { "project_id", "user_id", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_assistant_conversations_user_id",
                table: "assistant_conversations",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_assistant_drafts_conversation_id",
                table: "assistant_drafts",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_assistant_drafts_project_id_status",
                table: "assistant_drafts",
                columns: new[] { "project_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_entity_type_entity_id",
                table: "audit_entries",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_project_id_at",
                table: "audit_entries",
                columns: new[] { "project_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_points_updated_by",
                table: "connection_points",
                column: "updated_by");

            migrationBuilder.CreateIndex(
                name: "ix_design_runs_created_by",
                table: "design_runs",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_design_runs_job_id",
                table: "design_runs",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_design_runs_parent_run_id",
                table: "design_runs",
                column: "parent_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_design_runs_project_id_number",
                table: "design_runs",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_created_by",
                table: "documents",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_documents_design_run_id",
                table: "documents",
                column: "design_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_project_id_superseded_at",
                table: "documents",
                columns: new[] { "project_id", "superseded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_revision_id",
                table: "documents",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_rate_libraries_active",
                table: "rate_libraries",
                column: "active");

            migrationBuilder.CreateIndex(
                name: "ix_rate_libraries_imported_by",
                table: "rate_libraries",
                column: "imported_by");

            migrationBuilder.CreateIndex(
                name: "ix_rate_overrides_created_by",
                table: "rate_overrides",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_rate_overrides_item_code",
                table: "rate_overrides",
                column: "item_code",
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_report_sections_project_id_key",
                table: "report_sections",
                columns: new[] { "project_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_revisions_created_by",
                table: "revisions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_revisions_design_run_id",
                table: "revisions",
                column: "design_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_revisions_project_id_number",
                table: "revisions",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_revisions_signed_off_by",
                table: "revisions",
                column: "signed_off_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_drafts");

            migrationBuilder.DropTable(
                name: "audit_entries");

            migrationBuilder.DropTable(
                name: "connection_points");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "rate_libraries");

            migrationBuilder.DropTable(
                name: "rate_overrides");

            migrationBuilder.DropTable(
                name: "report_sections");

            migrationBuilder.DropTable(
                name: "assistant_conversations");

            migrationBuilder.DropTable(
                name: "revisions");

            migrationBuilder.DropTable(
                name: "design_runs");
        }
    }
}
