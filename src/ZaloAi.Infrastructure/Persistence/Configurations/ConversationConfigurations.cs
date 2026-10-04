using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ZaloAi.Core.Entities;

namespace ZaloAi.Infrastructure.Persistence.Configurations;

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> builder)
    {
        builder.ToTable("contacts");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Channel).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ChannelKind>>();
        builder.Property(c => c.ExternalUserId).HasMaxLength(100);
        builder.Property(c => c.DisplayName).HasMaxLength(200);

        builder.HasIndex(c => new { c.TenantId, c.Channel, c.ExternalUserId }).IsUnique();

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Channel).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ChannelKind>>();
        builder.Property(c => c.Mode).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ConversationMode>>();
        builder.Property(c => c.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ConversationStatus>>();
        builder.Property(c => c.Urgency).HasMaxLength(20).HasConversion<LowercaseEnumConverter<Urgency>>();
        builder.Property(c => c.HandoffReason).HasMaxLength(200);

        builder.HasIndex(c => new { c.TenantId, c.ContactId });
        builder.HasIndex(c => new { c.TenantId, c.UpdatedAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(c => c.ContactId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.Property(m => m.Direction).HasMaxLength(10).HasConversion<LowercaseEnumConverter<MessageDirection>>();
        builder.Property(m => m.Sender).HasMaxLength(20).HasConversion<LowercaseEnumConverter<MessageSender>>();
        builder.Property(m => m.ExternalMessageId).HasMaxLength(100);
        builder.Property(m => m.AiTraceJson).HasColumnName("ai_trace").HasColumnType("jsonb");

        builder.HasIndex(m => new { m.TenantId, m.ConversationId, m.CreatedAt });

        // Chống xử lý trùng: kênh gửi lại cùng tin (B4: không trùng theo từng tenant).
        builder.HasIndex(m => new { m.TenantId, m.ExternalMessageId }).IsUnique().HasFilter("external_message_id IS NOT NULL");

        // Mỗi tin khách chỉ có một câu trả lời của bot, kể cả khi job chạy lại.
        builder.HasIndex(m => new { m.TenantId, m.ReplyToMessageId }).IsUnique().HasFilter("reply_to_message_id IS NOT NULL");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Message>().WithMany().HasForeignKey(m => m.ReplyToMessageId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<UsageKind>>();
        builder.Property(u => u.Provider).HasMaxLength(50);
        builder.Property(u => u.Model).HasMaxLength(100);
        builder.Property(u => u.CostUsd).HasPrecision(14, 8);

        // Báo cáo chi phí theo tenant/tháng (M6).
        builder.HasIndex(u => new { u.TenantId, u.CreatedAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(u => u.ConversationId).OnDelete(DeleteBehavior.SetNull);
    }
}
