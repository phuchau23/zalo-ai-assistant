using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZaloAi.IndustryTemplates;

/// <param name="Strict">true = luôn cấm; false = được dùng nếu đúng nguyên văn trong đoạn dữ liệu bot đã dùng.</param>
public sealed record ForbiddenPhrase(string Phrase, bool Strict, string? Replacement);

public sealed record DangerSignal(string Name, IReadOnlyList<string> Keywords);

/// <param name="Pii">Dữ liệu cá nhân → mã hóa khi lưu.</param>
public sealed record LeadField(string Key, string Label, bool Pii);

/// <summary>Một câu kiểm tra trong evals.json (docs/INDUSTRIES.md mục 5).</summary>
/// <param name="Category">normal | out_of_scope | safety</param>
/// <param name="Expect">answer (trả lời từ dữ liệu) | handoff (chuyển người) | urgent (chuyển khẩn cấp)</param>
/// <param name="MustContain">Mỗi phần tử: danh sách phương án, câu trả lời phải chứa ít nhất một (bỏ dấu, chữ thường).</param>
/// <param name="MustNotContain">Câu trả lời không được chứa (bỏ dấu, chữ thường).</param>
public sealed record EvalCase(
    string Id,
    string Category,
    string Question,
    string Expect,
    IReadOnlyList<IReadOnlyList<string>> MustContain,
    IReadOnlyList<string> MustNotContain,
    string? Note);

/// <summary>Mẫu ngành đã nạp (docs/INDUSTRIES.md mục 1). Không đổi được lúc chạy.</summary>
public sealed record IndustryTemplate(
    string Slug,
    string Name,
    string Risk,
    bool MedicalSafety,
    string Version,
    string CustomerAddress,
    string Persona,
    string Rules,
    IReadOnlyList<ForbiddenPhrase> Forbidden,
    string UrgentReply,
    IReadOnlyList<DangerSignal> DangerSignals,
    IReadOnlyList<LeadField> LeadFields,
    IReadOnlyList<EvalCase> Evals);

/// <summary>
/// Đọc mẫu ngành nhúng trong dll (Templates/&lt;slug&gt;/...). Ngành trong <see cref="IndustryCatalog"/> chưa có mẫu riêng
/// → dùng "_default" (chỉ quy tắc chung). Thiếu file bắt buộc → lỗi ngay khi khởi động (test bắt được).
/// </summary>
public static class IndustryTemplateRegistry
{
    public const string DefaultSlug = "_default";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<FrozenDictionary<string, IndustryTemplate>> Templates = new(Load);

    public static IReadOnlyCollection<IndustryTemplate> All => Templates.Value.Values;

    /// <summary>Mẫu riêng của ngành nếu có, không thì mẫu chung.</summary>
    public static IndustryTemplate Get(string? slug) =>
        slug is not null && Templates.Value.TryGetValue(slug, out var template) ? template : Templates.Value[DefaultSlug];

    public static bool HasOwnTemplate(string slug) => Templates.Value.ContainsKey(slug) && slug != DefaultSlug;

    private static FrozenDictionary<string, IndustryTemplate> Load()
    {
        var assembly = typeof(IndustryTemplateRegistry).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Path: name.Replace('\\', '/')))
            .Where(f => f.Path.StartsWith("Templates/", StringComparison.Ordinal))
            .ToList();

        var slugs = files.Select(f => f.Path.Split('/')[1]).Distinct(StringComparer.Ordinal);
        var result = new Dictionary<string, IndustryTemplate>(StringComparer.Ordinal);
        foreach (var slug in slugs)
        {
            string? Read(string file)
            {
                var match = files.FirstOrDefault(f => f.Path == $"Templates/{slug}/{file}");
                if (match.Name is null)
                {
                    return null;
                }

                using var stream = assembly.GetManifestResourceStream(match.Name)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }

            string Require(string file) => Read(file) ?? throw new InvalidOperationException($"Mẫu ngành '{slug}' thiếu file {file}.");
            T Parse<T>(string file) => JsonSerializer.Deserialize<T>(Require(file), Json)
                ?? throw new InvalidOperationException($"Mẫu ngành '{slug}': {file} rỗng.");

            var meta = Parse<TemplateFile>("template.json");
            if (meta.Slug != slug)
            {
                throw new InvalidOperationException($"Mẫu ngành '{slug}': slug trong template.json là '{meta.Slug}'.");
            }

            var danger = Parse<DangerFile>("danger_signals.json");
            var evalsJson = Read("evals.json");
            var evals = evalsJson is null ? [] : JsonSerializer.Deserialize<EvalsFile>(evalsJson, Json)?.Cases ?? [];

            result[slug] = new IndustryTemplate(
                meta.Slug,
                meta.Name,
                meta.Risk,
                meta.MedicalSafety,
                meta.Version,
                meta.CustomerAddress ?? "anh/chị",
                Require("persona.md").Trim(),
                Require("rules.md").Trim(),
                Parse<ForbiddenFile>("forbidden.json").Phrases,
                danger.UrgentReply,
                danger.Signals,
                Parse<LeadFieldsFile>("lead_fields.json").Fields,
                evals.Select(e => e with
                {
                    MustContain = e.MustContain ?? [],
                    MustNotContain = e.MustNotContain ?? [],
                }).ToList());
        }

        if (!result.ContainsKey(DefaultSlug))
        {
            throw new InvalidOperationException("Thiếu mẫu ngành _default.");
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record TemplateFile(string Slug, string Name, string Risk, bool MedicalSafety, string Version, string? CustomerAddress);

    private sealed record ForbiddenFile(List<ForbiddenPhrase> Phrases);

    private sealed record DangerFile(string UrgentReply, List<DangerSignal> Signals);

    private sealed record LeadFieldsFile(List<LeadField> Fields);

    private sealed record EvalsFile(List<EvalCase> Cases);
}
