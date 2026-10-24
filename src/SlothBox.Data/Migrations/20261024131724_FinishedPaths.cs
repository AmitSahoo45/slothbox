using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SlothBox.Data.Migrations
{
    /// <inheritdoc />
    public partial class FinishedPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "finished_paths",
                columns: table => new
                {
                    path = table.Column<string>(type: "citext", nullable: false),
                    folder_id = table.Column<int>(type: "integer", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    last_write_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_finished_paths", x => x.path);
                    table.ForeignKey(
                        name: "fk_finished_paths_folder",
                        column: x => x.folder_id,
                        principalTable: "watched_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_finished_paths_folder_id",
                table: "finished_paths",
                column: "folder_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "finished_paths");
        }
    }
}
