using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;
using Pgvector;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SlothBox.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "app_state",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_state", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    type = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    folder = table.Column<string>(type: "text", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.id);
                    table.CheckConstraint("ck_categories_type", "type IN ('receipt','screenshot','document','other')");
                });

            migrationBuilder.CreateTable(
                name: "ignored_hashes",
                columns: table => new
                {
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ignored_hashes", x => x.sha256);
                });

            migrationBuilder.CreateTable(
                name: "watched_folders",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    path = table.Column<string>(type: "citext", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    is_cloud = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_watched_folders", x => x.id);
                    table.CheckConstraint("ck_watched_folders_mode", "mode IN ('move','copy')");
                });

            migrationBuilder.CreateTable(
                name: "files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    watched_folder_id = table.Column<int>(type: "integer", nullable: true),
                    original_name = table.Column<string>(type: "text", nullable: false),
                    original_path = table.Column<string>(type: "text", nullable: false),
                    current_path = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    category_id = table.Column<int>(type: "integer", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    caption = table.Column<string>(type: "text", nullable: false),
                    extracted_text = table.Column<string>(type: "text", nullable: false),
                    file_date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    review_reason = table.Column<string>(type: "text", nullable: true),
                    duplicate_of = table.Column<Guid>(type: "uuid", nullable: true),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: true),
                    search = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false)
                        .Annotation("Npgsql:TsVectorConfig", "english")
                        .Annotation("Npgsql:TsVectorProperties", new[] { "title", "caption", "extracted_text" }),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_files", x => x.id);
                    table.CheckConstraint("ck_files_origin", "origin IN ('folder','phone')");
                    table.CheckConstraint("ck_files_review_reason", "review_reason IS NULL OR (status = 'review' AND review_reason IN ('bad_output','unknown_type','missing_date','missing_amount','heic','scanned_pdf','password_protected'))");
                    table.CheckConstraint("ck_files_status", "status IN ('filed','review','duplicate','undone')");
                    table.CheckConstraint("ck_files_type", "type IN ('receipt','screenshot','document','other','unknown')");
                    table.ForeignKey(
                        name: "fk_files_category",
                        column: x => x.category_id,
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_files_duplicate_of",
                        column: x => x.duplicate_of,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_files_watched_folder",
                        column: x => x.watched_folder_id,
                        principalTable: "watched_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "folder_baseline",
                columns: table => new
                {
                    folder_id = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "citext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folder_baseline", x => new { x.folder_id, x.path });
                    table.ForeignKey(
                        name: "fk_folder_baseline_folder",
                        column: x => x.folder_id,
                        principalTable: "watched_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "actions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    from_path = table.Column<string>(type: "text", nullable: false),
                    to_path = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actions", x => x.id);
                    table.CheckConstraint("ck_actions_kind", "kind IN ('move','copy','undo_move','delete_copy')");
                    table.CheckConstraint("ck_actions_status", "status IN ('pending','done','failed')");
                    table.ForeignKey(
                        name: "fk_actions_file",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    path = table.Column<string>(type: "citext", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    watched_folder_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_origin", "origin IN ('folder','phone')");
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued','processing','done','review','failed','skipped')");
                    table.ForeignKey(
                        name: "fk_jobs_file",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_jobs_watched_folder",
                        column: x => x.watched_folder_id,
                        principalTable: "watched_folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "receipts",
                columns: table => new
                {
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vendor = table.Column<string>(type: "text", nullable: true),
                    receipt_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "char(3)", nullable: true),
                    payment_method = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_receipts", x => x.file_id);
                    table.CheckConstraint("ck_receipts_payment_method", "payment_method IN ('upi','card','cash','netbanking','unknown')");
                    table.ForeignKey(
                        name: "fk_receipts_file",
                        column: x => x.file_id,
                        principalTable: "files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "categories",
                columns: new[] { "id", "created_at", "description", "folder", "is_system", "name", "type" },
                values: new object[,]
                {
                    { 1, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Restaurants, food delivery, groceries", "Food", false, "Food", "receipt" },
                    { 2, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Online and store purchases", "Shopping", false, "Shopping", "receipt" },
                    { 3, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Electricity, mobile, internet, rent, subscriptions", "Bills and recharges", false, "Bills and recharges", "receipt" },
                    { 4, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Tickets, cabs, fuel, hotels", "Travel", false, "Travel", "receipt" },
                    { 5, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Anything that fits no other category", "Other", true, "Other", "receipt" },
                    { 6, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Reddit posts and comment threads", "Reddit comments", false, "Reddit comments", "screenshot" },
                    { 7, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Instagram posts, comments, DMs", "Instagram comments", false, "Instagram comments", "screenshot" },
                    { 8, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "WhatsApp, Telegram, SMS and other chats", "Chats", false, "Chats", "screenshot" },
                    { 9, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Code, terminals, stack traces, error dialogs", "Code and errors", false, "Code and errors", "screenshot" },
                    { 10, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Anything that fits no other category", "Other", true, "Other", "screenshot" },
                    { 11, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Work documents and reports", "Work", false, "Work", "document" },
                    { 12, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Statements, tax, insurance, salary", "Finance", false, "Finance", "document" },
                    { 13, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Articles, notes, courses, documentation", "Learning", false, "Learning", "document" },
                    { 14, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "IDs, letters, personal records", "Personal", false, "Personal", "document" },
                    { 15, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Anything that fits no other category", "Other", true, "Other", "document" },
                    { 16, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Installers", "Installers", true, "Installers", "other" },
                    { 17, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Archives", "Archives", true, "Archives", "other" },
                    { 18, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Media", "Media", true, "Media", "other" },
                    { 19, new DateTimeOffset(new DateTime(2026, 10, 20, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Other file extensions", "Misc", true, "Misc", "other" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_actions_file_id",
                table: "actions",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_categories_type_name",
                table: "categories",
                columns: new[] { "type", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_files_category_id",
                table: "files",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_files_duplicate_of",
                table: "files",
                column: "duplicate_of");

            migrationBuilder.CreateIndex(
                name: "ix_files_embedding",
                table: "files",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_files_search",
                table: "files",
                column: "search")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_files_sha256",
                table: "files",
                column: "sha256");

            migrationBuilder.CreateIndex(
                name: "ix_files_status",
                table: "files",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_files_watched_folder_id",
                table: "files",
                column: "watched_folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_file_id",
                table: "jobs",
                column: "file_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_path_active",
                table: "jobs",
                column: "path",
                unique: true,
                filter: "status IN ('queued','processing')");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_watched_folder_id",
                table: "jobs",
                column: "watched_folder_id");

            migrationBuilder.CreateIndex(
                name: "ix_watched_folders_path",
                table: "watched_folders",
                column: "path",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "actions");

            migrationBuilder.DropTable(
                name: "app_state");

            migrationBuilder.DropTable(
                name: "folder_baseline");

            migrationBuilder.DropTable(
                name: "ignored_hashes");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "receipts");

            migrationBuilder.DropTable(
                name: "files");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "watched_folders");
        }
    }
}
