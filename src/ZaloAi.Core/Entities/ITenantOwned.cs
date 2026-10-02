namespace ZaloAi.Core.Entities;

/// <summary>
/// Entity thuộc về một tenant. DbContext tự áp global query filter và chặn ghi sai tenant cho mọi entity implement interface này.
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
