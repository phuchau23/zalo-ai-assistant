using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

internal static class KnowledgeSchema
{
    /// <summary>
    /// Số chiều cột vector. Phải khớp Ai:EmbedDim. Đổi số chiều = migration mới + đánh chỉ mục lại toàn bộ (skill db-migration).
    /// </summary>
    public const int EmbeddingDimensions = 768;
}

internal sealed class KnowledgeItemConfiguration : IEntityTypeConfiguration<KnowledgeItem>
{
    public void Configure(EntityTypeBuilder<KnowledgeItem> builder)
    {
        builder.ToTable("knowledge_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<KnowledgeKind>>();
        builder.Property(i => i.Code).HasMaxLength(50);
        builder.Property(i => i.DataJson).HasColumnName("data").HasColumnType("jsonb");
        builder.Property(i => i.ContentHash).HasMaxLength(64);

        // Mã không trùng trong một doanh nghiệp; index này cũng phục vụ lọc theo tenant.
        builder.HasIndex(i => new { i.TenantId, i.Code }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.Kind });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.UpdatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class KnowledgeImportConfiguration : IEntityTypeConfiguration<KnowledgeImport>
{
    public void Configure(EntityTypeBuilder<KnowledgeImport> builder)
    {
        builder.ToTable("knowledge_imports");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();

        builder.Property(i => i.FileName).HasMaxLength(255);
        builder.Property(i => i.SourceFormat).HasMaxLength(10).HasConversion<LowercaseEnumConverter<KnowledgeImportFormat>>();
        builder.Property(i => i.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<KnowledgeImportStatus>>();
        builder.Property(i => i.DiffJson).HasColumnName("diff").HasColumnType("jsonb");
        builder.Property(i => i.SelectionJson).HasColumnName("selection").HasColumnType("jsonb");

        // Lịch sử nhập: mới nhất trước.
        builder.HasIndex(i => new { i.TenantId, i.CreatedAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.CreatedBy).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.AppliedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class KnowledgeDocumentConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.Title).HasMaxLength(300);
        builder.Property(d => d.FileName).HasMaxLength(255);
        builder.Property(d => d.ContentType).HasMaxLength(150);
        builder.Property(d => d.StorageKey).HasMaxLength(500);
        builder.Property(d => d.Sha256).HasMaxLength(64);
        builder.Property(d => d.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<DocumentStatus>>();
        builder.Property(d => d.Error).HasMaxLength(1000);

        // Nạp lại cùng tên file = thay thế.
        builder.HasIndex(d => new { d.TenantId, d.FileName }).IsUnique();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ChunkConfiguration : IEntityTypeConfiguration<Chunk>
{
    public void Configure(EntityTypeBuilder<Chunk> builder)
    {
        builder.ToTable("chunks", t =>
            t.HasCheckConstraint("ck_chunks_one_source", "(knowledge_item_id IS NULL) <> (document_id IS NULL)"));
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.MetaJson).HasColumnName("meta").HasColumnType("jsonb");
        builder.Property(c => c.EmbeddingModel).HasMaxLength(100);

        // Core giữ float[] (không phụ thuộc thư viện pgvector); cột Postgres là vector(768).
        builder.Property(c => c.Embedding)
            .HasColumnType($"vector({KnowledgeSchema.EmbeddingDimensions})")
            .HasConversion(
                v => v == null ? null : new Vector(v),
                v => v == null ? null : v.ToArray(),
                new ValueComparer<float[]?>(
                    (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
                    v => v == null ? 0 : v.Length,
                    v => v == null ? null : v.ToArray()));

        builder.HasIndex(c => c.TenantId);
        builder.HasIndex(c => c.KnowledgeItemId);
        builder.HasIndex(c => c.DocumentId);

        // HNSW cho tìm kiếm gần đúng theo cosine. Truy vấn luôn lọc tenant_id (ChunkRepository.SearchAsync).
        builder.HasIndex(c => c.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeItem>().WithMany().HasForeignKey(c => c.KnowledgeItemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}
