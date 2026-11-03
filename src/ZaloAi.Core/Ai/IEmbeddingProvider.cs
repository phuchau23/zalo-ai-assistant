namespace ZaloAi.Core.Ai;

/// <summary>Một đoạn cần tạo vector. <see cref="Title"/> giúp model hiểu ngữ cảnh (tên dịch vụ, tiêu đề tài liệu).</summary>
public sealed record EmbeddingDocument(string? Title, string Text);

/// <summary>
/// Tạo vector (embedding) cho tìm kiếm ngữ nghĩa. Tách khỏi chat (Ai:ChatProvider) vì có thể dùng nhà cung cấp khác
/// (Ai:EmbedProvider). Đổi model/số chiều = phải đánh chỉ mục lại toàn bộ (skill db-migration, add-ai-provider).
/// Lỗi gọi ra ngoài ném <see cref="Errors.AiProviderException"/> để job Hangfire thử lại.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Tên model, lưu cùng vector (chunks.embedding_model).</summary>
    string ModelName { get; }

    /// <summary>Vector cho các đoạn tài liệu, cùng thứ tự đầu vào.</summary>
    Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<EmbeddingDocument> documents, CancellationToken cancellationToken);

    /// <summary>Vector cho câu hỏi tìm kiếm (định dạng khác tài liệu để tìm chính xác hơn).</summary>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken);
}
