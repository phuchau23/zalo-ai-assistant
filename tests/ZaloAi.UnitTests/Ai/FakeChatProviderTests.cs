using System.Text.Json;
using Shouldly;
using ZaloAi.Ai.Providers;
using ZaloAi.Core.Ai;

namespace ZaloAi.UnitTests.Ai;

/// <summary>AI giả phải mô phỏng được ý định đặt lịch (CRM C2) để test tích hợp chạy không cần Gemini.</summary>
public sealed class FakeChatProviderTests
{
    private const string System = """
        Bạn là trợ lý.
        ## ĐẶT LỊCH QUA CHAT (đang bật)
        - LỊCH SẮP TỚI của khách:
          • id=0199a000-0000-7000-8000-000000000123 — Thứ Ba, 20/10/2026 15:00 — Massage — CN1
        ## DỮ LIỆU THAM KHẢO
        [C1] (mã DV-MASSAGE-60) Massage Đông y 60 phút
        Giá 450.000 đ
        """;

    private static async Task<JsonElement> AskAsync(string system, string message)
    {
        var result = await new FakeChatProvider().ChatAsync(new ChatRequest(system, [new ChatTurn(ChatRole.User, message)], "{\"messages\":[]}", 0, 100), CancellationToken.None);
        return JsonDocument.Parse(result.Text).RootElement;
    }

    [Fact]
    public async Task Book_intent_from_time_and_chunk_code()
    {
        var root = await AskAsync(System, "chị muốn đặt lịch massage đông y 60 phút 15h ngày mai");
        var booking = root.GetProperty("booking");
        booking.GetProperty("action").GetString().ShouldBe("book");
        booking.GetProperty("service_codes")[0].GetString().ShouldBe("DV-MASSAGE-60");
        booking.GetProperty("time").GetString().ShouldBe("15:00");
        booking.GetProperty("party_size").GetInt32().ShouldBe(1);
        root.GetProperty("messages")[0].GetString()!.ShouldContain("kiểm tra lịch");
        root.GetProperty("customer_salutation").GetString().ShouldBe("chị");
    }

    [Fact]
    public async Task Cancel_and_reschedule_use_upcoming_appointment_id()
    {
        (await AskAsync(System, "chị muốn hủy lịch")).GetProperty("booking").GetProperty("appointment_id").GetString().ShouldBe("0199a000-0000-7000-8000-000000000123");
        var move = (await AskAsync(System, "dời lịch sang 16h")).GetProperty("booking");
        move.GetProperty("action").GetString().ShouldBe("reschedule");
        move.GetProperty("time").GetString().ShouldBe("16:00");
    }

    [Fact]
    public async Task No_booking_section_means_no_intent()
    {
        var root = await AskAsync("Bạn là trợ lý.\n[C1] (mã DV-MASSAGE-60) Massage\nGiá 450.000 đ", "đặt lịch massage 15h");
        root.TryGetProperty("booking", out _).ShouldBeFalse();
    }
}
