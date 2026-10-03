using ZaloAi.Infrastructure.Knowledge;

namespace ZaloAi.Api.Cli;

/// <summary>
/// Lệnh dòng lệnh làm việc với file mẫu kiến thức, không cần database:
///   dotnet run --project src/ZaloAi.Api -- knowledge validate &lt;file.xlsx|file.json&gt;
///   dotnet run --project src/ZaloAi.Api -- knowledge convert &lt;vào.json|vào.xlsx&gt; &lt;ra.xlsx|ra.json&gt;
/// Dùng để kiểm tra file doanh nghiệp/AI gửi trước khi nhập, và chuyển JSON (AI tạo) ↔ Excel (người sửa).
/// </summary>
internal static class KnowledgeCli
{
    public static bool IsKnowledgeCommand(string[] args) => args is ["knowledge", ..];

    public static int Run(string[] args)
    {
        switch (args)
        {
            case ["knowledge", "validate", var input]:
                return Validate(input);
            case ["knowledge", "convert", var input, var output]:
                return Convert(input, output);
            case ["knowledge", "diff", var oldFile, var newFile]:
                return Diff(oldFile, newFile);
            default:
                Console.WriteLine("Cách dùng:");
                Console.WriteLine("  knowledge validate <file.xlsx|file.json>");
                Console.WriteLine("  knowledge convert <vào.json|vào.xlsx> <ra.xlsx|ra.json>");
                Console.WriteLine("  knowledge diff <cũ.xlsx|cũ.json> <mới.xlsx|mới.json>");
                return 2;
        }
    }

    /// <summary>So sánh hai file như khi nhập file mới lên hệ thống (file cũ đóng vai dữ liệu đang có), không cần database.</summary>
    private static int Diff(string oldFile, string newFile)
    {
        if (Read(oldFile) is not { } before || Read(newFile) is not { } after)
        {
            return 2;
        }

        PrintErrors(before);
        PrintErrors(after);
        if (!before.IsValid || !after.IsValid)
        {
            return 1;
        }

        var diff = KnowledgeDiffer.Compare(after.Entries, before.Entries.Select(e => KnowledgeItemMapper.ToItem(e, null)).ToList());
        var s = diff.Summary;
        Console.WriteLine($"Thêm mới: {s.Added} | Thay đổi: {s.Changed} | Có thể trùng: {s.PossibleDuplicate} | Không còn trong file mới: {s.Missing} | Không đổi: {s.Unchanged}");

        foreach (var entry in diff.Entries)
        {
            var label = entry.Type switch
            {
                KnowledgeChangeType.Added => "THÊM",
                KnowledgeChangeType.Changed => "ĐỔI",
                KnowledgeChangeType.PossibleDuplicate => $"CÓ THỂ TRÙNG với {entry.ExistingCode} \"{entry.ExistingTitle}\" ({entry.Similarity:P0})",
                _ => "KHÔNG CÒN",
            };
            Console.WriteLine();
            Console.WriteLine($"[{label}] {entry.Code} — {entry.Title}");
            if (entry.Type is KnowledgeChangeType.Changed or KnowledgeChangeType.PossibleDuplicate)
            {
                foreach (var change in entry.Changes)
                {
                    Console.WriteLine($"    {change.Label}: {change.Old ?? "(trống)"}  →  {change.New ?? "(trống)"}");
                }
            }
        }

        return 0;
    }

    private static int Validate(string input)
    {
        if (Read(input) is not { } result)
        {
            return 2;
        }

        PrintErrors(result);
        if (!result.IsValid)
        {
            return 1;
        }

        var byKind = KnowledgeTemplate.Kinds
            .Select(k => $"{k.Label}: {result.Entries.Count(e => e.Kind == k.Kind)}");
        Console.WriteLine($"Hợp lệ. {result.Entries.Count} mục ({string.Join(", ", byKind)}).");
        return 0;
    }

    private static int Convert(string input, string output)
    {
        if (Read(input) is not { } result)
        {
            return 2;
        }

        PrintErrors(result);
        if (!result.IsValid)
        {
            Console.WriteLine("Không chuyển vì file vào còn lỗi. Sửa các lỗi trên rồi chạy lại.");
            return 1;
        }

        var extension = Path.GetExtension(output).ToLowerInvariant();
        if (extension is not (".xlsx" or ".json"))
        {
            Console.WriteLine("File ra phải có đuôi .xlsx hoặc .json.");
            return 2;
        }

        using (var stream = File.Create(output))
        {
            if (extension == ".json")
            {
                KnowledgeItemMapper.WriteJsonFile(result.Entries, stream);
            }
            else
            {
                KnowledgeExcel.Write(result.Entries, stream);
            }
        }

        Console.WriteLine($"Đã ghi {result.Entries.Count} mục vào {output}");
        return 0;
    }

    private static KnowledgeParseResult? Read(string input)
    {
        if (!File.Exists(input))
        {
            Console.WriteLine($"Không thấy file: {input}");
            return null;
        }

        using var stream = File.OpenRead(input);
        switch (Path.GetExtension(input).ToLowerInvariant())
        {
            case ".json":
                return KnowledgeJsonReader.Read(stream);
            case ".xlsx":
                return KnowledgeExcel.Read(stream);
            default:
                Console.WriteLine("Chỉ đọc được file .xlsx hoặc .json theo mẫu.");
                return null;
        }
    }

    private static void PrintErrors(KnowledgeParseResult result)
    {
        foreach (var error in result.Errors)
        {
            var row = error.Row is { } r ? $", dòng {r}" : "";
            var column = error.Column is { } c ? $", cột \"{c}\"" : "";
            Console.WriteLine($"[Lỗi] {error.Location}{row}{column}: {error.Message}");
        }
    }
}
