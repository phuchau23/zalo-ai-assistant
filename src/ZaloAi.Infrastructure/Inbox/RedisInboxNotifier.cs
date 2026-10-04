using System.Text.Json;
using StackExchange.Redis;
using ZaloAi.Core.Realtime;

namespace ZaloAi.Infrastructure.Inbox;

/// <summary>Redis pub/sub: kênh "zaloai:inbox:{tenantId}". Lỗi Redis không làm hỏng việc chính (chỉ mất cập nhật realtime, FE vẫn tải lại định kỳ).</summary>
public sealed class RedisInboxNotifier(IConnectionMultiplexer redis) : IInboxNotifier
{
    public static RedisChannel ChannelFor(Guid tenantId) => RedisChannel.Literal($"zaloai:inbox:{tenantId:N}");

    public async Task PublishAsync(Guid tenantId, InboxEvent inboxEvent, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetSubscriber().PublishAsync(ChannelFor(tenantId), JsonSerializer.Serialize(inboxEvent, JsonSerializerOptions.Web));
        }
        catch (RedisException)
        {
            // Realtime là "có thì tốt": không ném lỗi làm hỏng việc lưu/trả lời tin.
        }
    }
}
