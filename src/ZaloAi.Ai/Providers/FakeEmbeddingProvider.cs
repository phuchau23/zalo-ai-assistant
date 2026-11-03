using System.Globalization;
using System.Text;
using ZaloAi.Core.Ai;
using ZaloAi.Core.Options;

namespace ZaloAi.Ai.Providers;

/// <summary>
/// Vector giả, tính tại chỗ, không gọi mạng — cho test và chạy dev khi không có key (Ai:EmbedProvider = fake).
/// Mỗi từ (bỏ dấu, chữ thường) cộng vào một chiều theo hash → câu có nhiều từ chung thì gần nhau.
/// Chất lượng kém xa model thật; KHÔNG dùng ở production.
/// </summary>
public sealed class FakeEmbeddingProvider : IEmbeddingProvider
{
    public string ModelName => "fake-bag-of-words";

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<EmbeddingDocument> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        IReadOnlyList<float[]> vectors = documents.Select(d => Embed($"{d.Title} {d.Text}")).ToList();
        return Task.FromResult(vectors);
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken) => Task.FromResult(Embed(query));

    public static float[] Embed(string text)
    {
        var vector = new float[AiOptions.SupportedEmbedDimensions];
        foreach (var word in Words(text ?? ""))
        {
            vector[(int)(Fnv1a(word) % (uint)vector.Length)] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        if (norm == 0)
        {
            vector[0] = 1f; // tránh vector 0 (cosine không xác định)
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    private static string[] Words(string text)
    {
        var plain = new StringBuilder();
        foreach (var ch in text.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                plain.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            }
        }

        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var ch in value)
        {
            hash = (hash ^ ch) * 16777619u;
        }

        return hash;
    }
}
