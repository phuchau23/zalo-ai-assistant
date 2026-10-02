using System.ComponentModel.DataAnnotations;

namespace ZaloAi.Core.Options;

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Worker hỏi hàng đợi bao lâu một lần. Nhỏ = tin nhắn được xử lý sớm hơn, nhưng truy vấn DB nhiều hơn.</summary>
    [Range(1, 60)]
    public int QueuePollSeconds { get; set; } = 5;

    /// <summary>Số job chạy song song trên một Worker.</summary>
    [Range(1, 100)]
    public int WorkerCount { get; set; } = 10;
}
