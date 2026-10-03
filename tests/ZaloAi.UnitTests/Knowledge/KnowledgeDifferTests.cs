using System.Text;
using Shouldly;
using ZaloAi.Infrastructure.Knowledge;

namespace ZaloAi.UnitTests.Knowledge;

public sealed class KnowledgeDifferTests
{
    private static IReadOnlyList<KnowledgeEntry> Entries(string json)
    {
        var result = KnowledgeJsonReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        result.Errors.ShouldBeEmpty();
        return result.Entries;
    }

    private static readonly IReadOnlyList<KnowledgeEntry> Existing = Entries("""
        {
          "services": [
            { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "price": 450000 },
            { "code": "DV-MASSAGE-90", "name": "Massage Đông y 90 phút", "price": 550000 },
            { "code": "DV-XONG-HOI", "name": "Xông hơi thảo dược", "price": 120000 }
          ],
          "faqs": [ { "code": "FAQ-DAT-LICH", "question": "Có cần đặt lịch?", "answer": "Nên đặt trước." } ]
        }
        """);

    private static KnowledgeDiff Compare(string incomingJson) =>
        KnowledgeDiffer.Compare(Entries(incomingJson), Existing.Select(e => KnowledgeItemMapper.ToItem(e, null)).ToList());

    [Fact]
    public void Detects_added_changed_unchanged_missing_and_possible_duplicate()
    {
        var diff = Compare("""
            {
              "services": [
                { "code": "DV-MASSAGE-60", "name": "Massage Đông y 60 phút", "price": 490000 },
                { "code": "DV-MASSAGE-DY-90P", "name": "Massage Đông Y 90 phút", "price": 590000 },
                { "code": "DV-NGAM-CHAN", "name": "Ngâm chân thảo dược", "price": 99000 }
              ],
              "faqs": [ { "code": "FAQ-DAT-LICH", "question": "Có cần đặt lịch?", "answer": "Nên đặt trước." } ]
            }
            """);

        diff.Summary.ShouldBe(new KnowledgeDiffSummary(Added: 1, Changed: 1, PossibleDuplicate: 1, Missing: 2, Unchanged: 1));

        var changed = diff.Entries.Single(e => e.Type == KnowledgeChangeType.Changed);
        changed.Code.ShouldBe("DV-MASSAGE-60");
        changed.DefaultSelected.ShouldBeTrue();
        changed.Changes.ShouldHaveSingleItem().ShouldBe(new KnowledgeFieldChange("price", "Giá (VNĐ)", "money", "450000", "490000"));

        var duplicate = diff.Entries.Single(e => e.Type == KnowledgeChangeType.PossibleDuplicate);
        duplicate.Code.ShouldBe("DV-MASSAGE-DY-90P");
        duplicate.ExistingCode.ShouldBe("DV-MASSAGE-90"); // "Đông Y" vs "Đông y": khác hoa thường vẫn nhận ra
        duplicate.DefaultSelected.ShouldBeFalse();
        duplicate.Similarity.ShouldBe(1.0);
        duplicate.Changes.ShouldContain(c => c.Field == "price" && c.Old == "550000" && c.New == "590000");

        diff.Entries.Single(e => e.Type == KnowledgeChangeType.Added).Code.ShouldBe("DV-NGAM-CHAN");

        var missing = diff.Entries.Where(e => e.Type == KnowledgeChangeType.Missing).ToList();
        missing.Select(e => e.Code).ShouldBe(["DV-MASSAGE-90", "DV-XONG-HOI"]);
        missing.ShouldAllBe(e => !e.DefaultSelected && e.Key.StartsWith("missing:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Massage Đông y 120 phút")] // số khác → mục khác, không phải trùng
    [InlineData("Gội đầu dưỡng sinh 90 phút")]
    public void Different_numbers_or_words_are_not_duplicates(string name)
    {
        var diff = Compare($$"""{ "services": [ { "code": "DV-MOI", "name": "{{name}}" } ] }""");

        diff.Entries.Single(e => e.Code == "DV-MOI").Type.ShouldBe(KnowledgeChangeType.Added);
    }

    [Fact]
    public void Small_typo_in_name_is_flagged_as_possible_duplicate()
    {
        var diff = Compare("""{ "services": [ { "code": "DV-XONG-HOI-2", "name": "Xông hơi thảo dượt" } ] }""");

        var duplicate = diff.Entries.Single(e => e.Code == "DV-XONG-HOI-2");
        duplicate.Type.ShouldBe(KnowledgeChangeType.PossibleDuplicate);
        duplicate.ExistingCode.ShouldBe("DV-XONG-HOI");
        duplicate.Similarity!.Value.ShouldBeGreaterThanOrEqualTo(KnowledgeDiffer.DuplicateThreshold);
    }

    [Fact]
    public void Duplicate_only_compared_within_same_kind()
    {
        var diff = Compare("""{ "policies": [ { "code": "CS-X", "topic": "Xông hơi thảo dược", "content": "..." } ] }""");

        diff.Entries.Single(e => e.Code == "CS-X").Type.ShouldBe(KnowledgeChangeType.Added);
    }

    [Fact]
    public void Identical_file_has_no_entries_except_unchanged_count()
    {
        var diff = KnowledgeDiffer.Compare(Existing, Existing.Select(e => KnowledgeItemMapper.ToItem(e, null)).ToList());

        diff.Entries.ShouldBeEmpty();
        diff.Summary.Unchanged.ShouldBe(4);
    }
}
