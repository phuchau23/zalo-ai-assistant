namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Tên hàng đợi Hangfire (chữ thường, không dấu gạch ngang). Thêm hàng đợi mới thì thêm vào <see cref="All"/>.</summary>
public static class JobQueues
{
    public const string Default = "default";

    public static readonly string[] All = [Default];
}
