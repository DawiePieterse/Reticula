using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reticula.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LoadPointClassOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "class_override",
                table: "load_points",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "class_override",
                table: "load_points");
        }
    }
}
