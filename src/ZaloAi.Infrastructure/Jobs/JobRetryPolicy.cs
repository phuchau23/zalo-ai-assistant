using Hangfire;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>Chính sách thử lại mặc định cho mọi job (CLAUDE.md mục 8: retry exponential backoff, hết lượt → dead-letter).</summary>
public static class JobRetryPolicy
{
    public const int Attempts = 5;

    /// <summary>Mỗi lần chờ gấp 3 lần trước: 10s, 30s, 1.5 phút, 4.5 phút, 13.5 phút (tổng ~20 phút).</summary>
    public static readonly int[] DelaysInSeconds = [10, 30, 90, 270, 810];

    /// <summary>
    /// Hết lượt → job ở trạng thái Failed (dead-letter): không bị xóa, xem và chạy lại được trên dashboard.
    /// <see cref="JobFailureAlertFilter"/> ghi log Error lúc đó.
    /// </summary>
    public static AutomaticRetryAttribute Create() => new()
    {
        Attempts = Attempts,
        DelaysInSeconds = DelaysInSeconds,
        OnAttemptsExceeded = AttemptsExceededAction.Fail,
        LogEvents = true,
    };
}
