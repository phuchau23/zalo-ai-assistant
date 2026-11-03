using Hangfire.Logging;
using Hangfire.States;
using Hangfire.Storage;

namespace ZaloAi.Infrastructure.Jobs;

/// <summary>
/// Job vào trạng thái Failed (đã hết lượt thử lại, vì các lần lỗi trước đó chuyển sang Scheduled) → log Error.
/// Bước 7 nối log Error sang Sentry để có cảnh báo; M5 thêm Telegram.
/// Chỉ ghi tên job và loại exception: tham số job và message lỗi có thể chứa dữ liệu khách.
/// </summary>
public sealed class JobFailureAlertFilter : IApplyStateFilter
{
    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.NewState is FailedState failed)
        {
            var job = context.BackgroundJob.Job;
            // Lấy logger mỗi lần (không cache static): log provider của Hangfire đổi theo host đang chạy.
            LogProvider.GetLogger(typeof(JobFailureAlertFilter)).Log(
                LogLevel.Error,
                () => $"Job hết lượt thử lại, cần xử lý tay trên dashboard: id={context.BackgroundJob.Id} " +
                    $"job={job?.Type.Name}.{job?.Method.Name} lỗi={failed.Exception?.GetType().Name}",
                exception: null);
        }
    }

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction)
    {
    }
}
