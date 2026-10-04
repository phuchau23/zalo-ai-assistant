using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Channels;
using ZaloAi.Core.Coordination;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Options;
using ZaloAi.Core.Realtime;
using ZaloAi.Core.Security;
using ZaloAi.Core.Storage;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Coordination;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Jobs;
using ZaloAi.Infrastructure.Knowledge;
using ZaloAi.Infrastructure.Persistence;
using ZaloAi.Infrastructure.Repositories;
using ZaloAi.Infrastructure.Security;
using ZaloAi.Infrastructure.Storage;
using ZaloAi.Infrastructure.Tenancy;
using ZaloAi.Infrastructure.Zalo;

namespace ZaloAi.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Đăng ký options (validate khi khởi động), mã hóa, tenant context, DB, repository. Dùng chung cho Api và Worker.</summary>
    public static IServiceCollection AddZaloAiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddValidatedOptions<AppOptions>(configuration, AppOptions.SectionName);
        services.AddValidatedOptions<SecurityOptions>(configuration, SecurityOptions.SectionName);
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<JobsOptions>(configuration, JobsOptions.SectionName);
        services.AddValidatedOptions<AiOptions>(configuration, AiOptions.SectionName);
        services.AddValidatedOptions<StorageOptions>(configuration, StorageOptions.SectionName);
        services.AddValidatedOptions<ZaloOptions>(configuration, ZaloOptions.SectionName);
        services.AddValidatedOptions<TelegramOptions>(configuration, TelegramOptions.SectionName);

        services.AddSingleton<IFieldEncryptor, AesGcmFieldEncryptor>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseZaloAiPostgres(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Postgres));

        // Redis: kết nối lười, không chặn khởi động khi Redis tạm chưa sẵn (abortConnect=false), tự nối lại.
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var config = ConfigurationOptions.Parse(sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Redis);
            config.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(config);
        });
        services.AddSingleton<IDistributedStore, RedisDistributedStore>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<TenantRepository>();
        services.AddScoped<MembershipRepository>();
        services.AddScoped<AuditLogRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<AccessQueries>();
        services.AddScoped<KnowledgeItemRepository>();
        services.AddScoped<KnowledgeImportRepository>();
        services.AddScoped<KnowledgeDocumentRepository>();
        services.AddScoped<ChunkRepository>();
        services.AddScoped<ConversationRepository>();
        services.AddScoped<ChannelConnectionRepository>();
        services.AddScoped<ChannelConnectionLookup>();
        services.AddScoped<ZaloConnectionService>();
        services.AddScoped<ZaloWebhookIngestor>();
        services.AddScoped<IChannelAdapter, ZaloAdapter>();
        services.AddScoped<ZaloTokenSweepJob>();
        services.AddScoped<ZaloTokenRefreshJob>();

        // Hộp thư (M5)
        services.AddSingleton<IInboxNotifier, RedisInboxNotifier>();
        services.AddScoped<HandoffSettingsRepository>();
        services.AddScoped<MessageDeliveryService>();
        services.AddScoped<InboxService>();
        services.AddScoped<SendOutgoingMessageJob>();
        services.AddScoped<AttentionNotifyJob>();
        services.AddScoped<AttentionReminderSweepJob>();
        services.AddScoped<AttentionReminderJob>();
        services.AddHttpClient<TelegramClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .AddStandardResilienceHandler(resilience =>
            {
                resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
                resilience.Retry.DisableForUnsafeHttpMethods(); // không gửi trùng thông báo
            });

        // Khách hàng + Cần chăm sóc + kết nối Telegram tự phục vụ (M6)
        services.AddScoped<ContactRepository>();
        services.AddScoped<CareAnalysisJob>();
        services.AddScoped<CareSweepJob>();
        services.AddScoped<CareFollowUpSweepJob>();
        services.AddScoped<ProactiveSendJob>();
        services.AddScoped<TelegramLinkService>();
        services.AddHttpClient<TelegramUpdatesClient>(http => http.Timeout = TimeSpan.FromSeconds(TelegramUpdatesClient.PollSeconds + 15));

        services.AddScoped<KnowledgeImportService>();
        services.AddScoped<KnowledgeIndexer>();
        services.AddScoped<IKnowledgeSearch, KnowledgeSearchService>();
        services.AddScoped<IndexKnowledgeJob>();
        services.AddScoped<ProcessIncomingMessageJob>();
        services.AddScoped<IndexSweepJob>();
        services.AddScoped<KnowledgeDocumentService>();
        services.AddScoped<IngestDocumentJob>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.AddZaloAiHangfire();
        services.AddScoped<SampleTenantJob>();

        return services;
    }

    /// <summary>Chỉ Worker: đọc lệnh "/ketnoi MÃ" gửi cho bot Telegram.</summary>
    public static IServiceCollection AddZaloAiTelegramPolling(this IServiceCollection services)
    {
        services.AddHostedService<TelegramPollingService>();
        return services;
    }

    private static void AddValidatedOptions<T>(this IServiceCollection services, IConfiguration configuration, string section)
        where T : class
    {
        services.AddOptions<T>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
