using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SlothBox.Core;
using System.Text.Json;

namespace SlothBox.Data;

public sealed class SlothBoxDbContext(DbContextOptions<SlothBoxDbContext> options) : DbContext(options)
{
    public DbSet<StoredCategory> Categories => Set<StoredCategory>();
    public DbSet<StoredWatchedFolder> WatchedFolders => Set<StoredWatchedFolder>();
    public DbSet<FolderBaseline> FolderBaselines => Set<FolderBaseline>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<StoredReceipt> Receipts => Set<StoredReceipt>();
    public DbSet<StoredAction> Actions => Set<StoredAction>();
    public DbSet<StoredJob> Jobs => Set<StoredJob>();
    public DbSet<StoredIgnoredHash> IgnoredHashes => Set<StoredIgnoredHash>();
    public DbSet<StoredAppState> AppState => Set<StoredAppState>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");
        model.HasPostgresExtension("citext");

        var categories = model.Entity<StoredCategory>();
        categories.ToTable("categories", table =>
            table.HasCheckConstraint("ck_categories_type", "type IN ('receipt','screenshot','document','other')"));
        categories.HasKey(x => x.Id).HasName("pk_categories");
        categories.Property(x => x.Type).HasConversion(new SnakeCaseEnumConverter<FileKind>());
        categories.HasIndex(x => new { x.Type, x.Name }).IsUnique().HasDatabaseName("ix_categories_type_name");
        categories.HasData(SeedCategories.All);

        var folders = model.Entity<StoredWatchedFolder>();
        folders.ToTable("watched_folders", table =>
            table.HasCheckConstraint("ck_watched_folders_mode", "mode IN ('move','copy')"));
        folders.HasKey(x => x.Id).HasName("pk_watched_folders");
        folders.Property(x => x.Mode).HasConversion(new SnakeCaseEnumConverter<FolderMode>());
        folders.Property(x => x.Path).HasColumnType("citext");
        folders.HasIndex(x => x.Path).IsUnique().HasDatabaseName("ix_watched_folders_path");

        var baseline = model.Entity<FolderBaseline>();
        baseline.ToTable("folder_baseline");
        baseline.HasKey(x => new { x.FolderId, x.Path }).HasName("pk_folder_baseline");
        baseline.Property(x => x.Path).HasColumnType("citext");
        baseline.HasOne<StoredWatchedFolder>().WithMany().HasForeignKey(x => x.FolderId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_folder_baseline_folder");

        var files = model.Entity<StoredFile>();
        files.ToTable("files", table =>
        {
            table.HasCheckConstraint("ck_files_type", "type IN ('receipt','screenshot','document','other','unknown')");
            table.HasCheckConstraint("ck_files_origin", "origin IN ('folder','phone')");
            table.HasCheckConstraint("ck_files_status", "status IN ('filed','review','duplicate','undone')");
            table.HasCheckConstraint("ck_files_review_reason",
                "review_reason IS NULL OR (status = 'review' AND review_reason IN ('bad_output','unknown_type','missing_date','missing_amount','heic','scanned_pdf','password_protected'))");
        });
        files.HasKey(x => x.Id).HasName("pk_files");
        files.Property(x => x.Type).HasConversion(new SnakeCaseEnumConverter<FileKind>());
        files.Property(x => x.Origin).HasConversion(new SnakeCaseEnumConverter<FileOrigin>());
        files.Property(x => x.Status).HasConversion(new SnakeCaseEnumConverter<FileStatus>());
        files.Property(x => x.ReviewReason).HasConversion(new SnakeCaseEnumConverter<ReviewReason>());
        files.Property(x => x.Embedding).HasColumnType("vector(768)");
        files.HasGeneratedTsVectorColumn(x => x.Search, "english", x => new { x.Title, x.Caption, x.ExtractedText });
        files.HasIndex(x => x.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops")
            .HasDatabaseName("ix_files_embedding");
        files.HasIndex(x => x.Search).HasMethod("gin").HasDatabaseName("ix_files_search");
        files.HasIndex(x => x.Sha256).HasDatabaseName("ix_files_sha256");
        files.HasIndex(x => x.Status).HasDatabaseName("ix_files_status");
        files.HasOne<StoredCategory>().WithMany().HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_files_category");
        files.HasOne<StoredWatchedFolder>().WithMany().HasForeignKey(x => x.WatchedFolderId)
            .OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_files_watched_folder");
        files.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.DuplicateOf)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_files_duplicate_of");

        var receipts = model.Entity<StoredReceipt>();
        receipts.ToTable("receipts", table => table.HasCheckConstraint("ck_receipts_payment_method",
            "payment_method IN ('upi','card','cash','netbanking','unknown')"));
        receipts.HasKey(x => x.FileId).HasName("pk_receipts");
        receipts.Property(x => x.Amount).HasPrecision(12, 2);
        receipts.Property(x => x.Currency).HasColumnType("char(3)");
        receipts.Property(x => x.PaymentMethod).HasConversion(new SnakeCaseEnumConverter<PaymentMethod>());
        receipts.HasOne<StoredFile>().WithOne().HasForeignKey<StoredReceipt>(x => x.FileId)
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_receipts_file");

        var actions = model.Entity<StoredAction>();
        actions.ToTable("actions", table =>
        {
            table.HasCheckConstraint("ck_actions_kind", "kind IN ('move','copy','undo_move','delete_copy')");
            table.HasCheckConstraint("ck_actions_status", "status IN ('pending','done','failed')");
        });
        actions.HasKey(x => x.Id).HasName("pk_actions");
        actions.Property(x => x.Kind).HasConversion(new SnakeCaseEnumConverter<ActionKind>());
        actions.Property(x => x.Status).HasConversion(new SnakeCaseEnumConverter<ActionStatus>());
        actions.Property(x => x.Before).HasColumnType("jsonb");
        actions.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_actions_file");

        var jobs = model.Entity<StoredJob>();
        jobs.ToTable("jobs", table =>
        {
            table.HasCheckConstraint("ck_jobs_status", "status IN ('queued','processing','done','review','failed','skipped')");
            table.HasCheckConstraint("ck_jobs_origin", "origin IN ('folder','phone')");
        });
        jobs.HasKey(x => x.Id).HasName("pk_jobs");
        jobs.Property(x => x.Origin).HasConversion(new SnakeCaseEnumConverter<FileOrigin>());
        jobs.Property(x => x.Status).HasConversion(new SnakeCaseEnumConverter<JobStatus>());
        jobs.Property(x => x.Path).HasColumnType("citext");
        jobs.HasIndex(x => x.Path).IsUnique().HasFilter("status IN ('queued','processing')")
            .HasDatabaseName("ix_jobs_path_active");
        jobs.HasOne<StoredWatchedFolder>().WithMany().HasForeignKey(x => x.WatchedFolderId)
            .OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_jobs_watched_folder");
        jobs.HasOne<StoredFile>().WithMany().HasForeignKey(x => x.FileId)
            .OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_jobs_file");

        var ignored = model.Entity<StoredIgnoredHash>();
        ignored.ToTable("ignored_hashes");
        ignored.HasKey(x => x.Sha256).HasName("pk_ignored_hashes");
        var state = model.Entity<StoredAppState>();
        state.ToTable("app_state");
        state.HasKey(x => x.Key).HasName("pk_app_state");

        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
                property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
            foreach (var index in entity.GetIndexes())
                index.SetDatabaseName(JsonNamingPolicy.SnakeCaseLower.ConvertName(index.GetDatabaseName()!));
        }
    }
}

internal sealed class SnakeCaseEnumConverter<T>() : ValueConverter<T, string>(
    value => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()),
    value => Enum.Parse<T>(value.Replace("_", ""), true)) where T : struct, Enum;
