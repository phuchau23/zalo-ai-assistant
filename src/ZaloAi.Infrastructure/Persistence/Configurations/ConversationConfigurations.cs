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

        builder.Property(c => c.LeadStatus).HasMaxLength(20).HasConversion<LowercaseEnumConverter<LeadStatus>>()
            .HasDefaultValue(LeadStatus.New).HasSentinel((LeadStatus)(-1));
        builder.Property(c => c.Tags).HasColumnType("text[]");
        builder.Property(c => c.ProactiveOptOutSource).HasMaxLength(20);

        builder.HasIndex(c => new { c.TenantId, c.Channel, c.ExternalUserId }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.LeadStatus });
        builder.HasIndex(c => new { c.TenantId, c.LastCustomerMessageAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ContactNoteConfiguration : IEntityTypeConfiguration<ContactNote>
{
    public void Configure(EntityTypeBuilder<ContactNote> builder)
    {
        builder.ToTable("contact_notes");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Kind).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ContactNoteKind>>();

        builder.HasIndex(n => new { n.TenantId, n.ContactId, n.HappenedOn });

        // Quét ngày hẹn chăm sóc lại (job hệ thống).
        builder.HasIndex(n => n.FollowUpAt).HasFilter("follow_up_at IS NOT NULL AND follow_up_queued_at IS NULL");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(n => n.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(n => n.ContactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(n => n.AuthorUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class CareSuggestionConfiguration : IEntityTypeConfiguration<CareSuggestion>
{
    public void Configure(EntityTypeBuilder<CareSuggestion> builder)
    {
        builder.ToTable("care_suggestions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Temperature).HasMaxLength(10).HasConversion<LowercaseEnumConverter<CareTemperature>>();
        builder.Property(s => s.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<CareSuggestionStatus>>();
        builder.Property(s => s.Trigger).HasMaxLength(40);
        builder.Property(s => s.Outcome).HasMaxLength(40);
        builder.Property(s => s.EscalationReason).HasMaxLength(40);
        builder.HasOne<Message>().WithMany().HasForeignKey(s => s.SentMessageId).OnDelete(DeleteBehavior.SetNull);

        // Job gửi tin chủ động đã hẹn giờ (quét dự phòng).
        builder.HasIndex(s => s.ScheduledSendAt).HasFilter("status = 'open' AND scheduled_send_at IS NOT NULL");

        builder.HasIndex(s => new { s.TenantId, s.Status, s.MessagingDeadline });
        builder.HasIndex(s => new { s.TenantId, s.ContactId });

        // Mỗi khách chỉ có tối đa 1 gợi ý đang mở (phân tích lại thì cập nhật gợi ý cũ).
        builder.HasIndex(s => new { s.TenantId, s.ContactId }).IsUnique().HasFilter("status = 'open'").HasDatabaseName("ix_care_suggestions_one_open_per_contact");

        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Contact>().WithMany().HasForeignKey(s => s.ContactId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Conversation>().WithMany().HasForeignKey(s => s.ConversationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.ResolvedByUserId).OnDelete(DeleteBehavior.SetNull);
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
        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(c => c.ConnectionId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(c => new { c.TenantId, c.ConnectionId, c.ContactId });
        builder.HasIndex(c => new { c.TenantId, c.LastMessageAt });
        builder.HasIndex(c => c.NeedsAttentionSince).HasFilter("needs_attention_since IS NOT NULL");
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.AssignedUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class HandoffSettingsConfiguration : IEntityTypeConfiguration<HandoffSettings>
{
    public void Configure(EntityTypeBuilder<HandoffSettings> builder)
    {
        builder.ToTable("handoff_settings");
        builder.HasKey(h => h.TenantId);
        builder.Property(h => h.HandoffMessage).HasMaxLength(500);
        builder.Property(h => h.AfterHoursMessage).HasMaxLength(500);
        builder.Property(h => h.TakeoverMessage).HasMaxLength(500);
        builder.Property(h => h.ReturnToBotMessage).HasMaxLength(500);
        builder.Property(h => h.ResponseTime).HasMaxLength(50);
        builder.Property(h => h.OpenTime).HasMaxLength(5);
        builder.Property(h => h.CloseTime).HasMaxLength(5);
        builder.Property(h => h.TelegramChatId).HasMaxLength(50);
        builder.Property(h => h.CareSendStart).HasMaxLength(5);
        builder.Property(h => h.CareSendEnd).HasMaxLength(5);
        builder.HasOne<Tenant>().WithMany().HasForeignKey(h => h.TenantId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChannelConnectionConfiguration : IEntityTypeConfiguration<ChannelConnection>
{
    public void Configure(EntityTypeBuilder<ChannelConnection> builder)
    {
        builder.ToTable("channel_connections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Channel).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ChannelKind>>();
        builder.Property(c => c.ExternalId).HasMaxLength(100);
        builder.Property(c => c.Name).HasMaxLength(200);
        builder.Property(c => c.Status).HasMaxLength(20).HasConversion<LowercaseEnumConverter<ConnectionStatus>>();
        builder.Property(c => c.LastError).HasMaxLength(100);

        // Một OA chỉ thuộc một DN: webhook tìm DN theo OA ID phải ra đúng 1 kết quả.
        builder.HasIndex(c => new { c.Channel, c.ExternalId }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.Channel });
        builder.HasIndex(c => new { c.Status, c.AccessTokenExpiresAt });

        builder.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Cascade);
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
        builder.Property(m => m.DeliveryStatus).HasMaxLength(20).HasConversion<LowercaseEnumConverter<DeliveryStatus>>()
            .HasDefaultValue(DeliveryStatus.None).HasSentinel((DeliveryStatus)(-1));
        builder.Property(m => m.DeliveryError).HasMaxLength(100);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.SenderUserId).OnDelete(DeleteBehavior.SetNull);

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
