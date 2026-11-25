namespace ZaloAi.Core.Ai;

public enum ChatRole
{
    User,
    Assistant,
}

public sealed record ChatTurn(ChatRole Role, string Text);

/// <param name="System">Hướng dẫn hệ thống (vai trò, quy tắc, dữ liệu tham khảo).</param>
/// <param name="JsonSchema">JSON Schema ép model trả JSON đúng cấu trúc; null = trả chữ thường.</param>
public sealed record ChatRequest(
    string System,
    IReadOnlyList<ChatTurn> Turns,
    string? JsonSchema = null,
    double Temperature = 0.2,
    int MaxOutputTokens = 2048);

/// <param name="InputTokens">Tổng token đầu vào, GỒM cả phần được nhà cung cấp cache.</param>
/// <param name="OutputTokens">Gồm cả token "suy nghĩ" của model (tính tiền như output).</param>
/// <param name="CachedInputTokens">Phần đầu vào trùng lần gọi trước, nhà cung cấp tính giá rẻ (Gemini cachedContentTokenCount,
/// Groq prompt_tokens_details.cached_tokens).</param>
public sealed record ChatResult(
    string Text,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    long LatencyMs,
    int CachedInputTokens = 0);

/// <summary>
/// Gọi model chat. Lỗi gọi ra ngoài ném <see cref="Errors.AiProviderException"/> (job thử lại; bot trả câu dự phòng + chuyển người).
/// </summary>
public interface IChatProvider
{
    Task<ChatResult> ChatAsync(ChatRequest request, CancellationToken cancellationToken);
}
