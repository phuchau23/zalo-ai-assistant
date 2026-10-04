using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using ZaloAi.Api.Auth;
using ZaloAi.Api.Common;
using ZaloAi.Core.Entities;
using ZaloAi.Core.Tenancy;
using ZaloAi.Infrastructure.Customers;
using ZaloAi.Infrastructure.Inbox;
using ZaloAi.Infrastructure.Repositories;

namespace ZaloAi.Api.Inbox;

/// <param name="WorkingDays">Bit 0 = Chủ nhật ... bit 6 = Thứ 7.</param>
/// <param name="TelegramConfigured">Hệ thống đã có bot Telegram chưa (chưa → ô chat id không có tác dụng).</param>
public sealed record HandoffSettingsResponse(
    string HandoffMessage,
    string AfterHoursMessage,
    bool TakeoverMessageEnabled,
    string TakeoverMessage,
    bool ReturnToBotMessageEnabled,
    string ReturnToBotMessage,
    bool StaffSignatureEnabled,
    string ResponseTime,
    string OpenTime,
    string CloseTime,
    int WorkingDays,
    int ReminderMinutes,
    string? TelegramChatId,
    bool TelegramConfigured,
    bool CareEnabled,
    int CareColdHours,
    bool CareAutoSend,
    string CareSendStart,
    string CareSendEnd)
{
    internal static HandoffSettingsResponse From(HandoffSettings s, bool telegram) => new(
        s.HandoffMessage, s.AfterHoursMessage, s.TakeoverMessageEnabled, s.TakeoverMessage, s.ReturnToBotMessageEnabled, s.ReturnToBotMessage,
        s.StaffSignatureEnabled, s.ResponseTime, s.OpenTime, s.CloseTime, s.WorkingDays, s.ReminderMinutes, s.TelegramChatId, telegram,
        s.CareEnabled, s.CareColdHours, s.CareAutoSend, s.CareSendStart, s.CareSendEnd);
}

public sealed record UpdateHandoffSettingsRequest(
    string HandoffMessage,
    string AfterHoursMessage,
    bool TakeoverMessageEnabled,
    string TakeoverMessage,
    bool ReturnToBotMessageEnabled,
    string ReturnToBotMessage,
    bool StaffSignatureEnabled,
    string ResponseTime,
    string OpenTime,
    string CloseTime,
    int WorkingDays,
    int ReminderMinutes,
    string? TelegramChatId,
    bool CareEnabled = true,
    int CareColdHours = 6,
    bool CareAutoSend = true,
    string CareSendStart = "08:00",
    string CareSendEnd = "20:00");

internal sealed partial class UpdateHandoffSettingsRequestValidator : AbstractValidator<UpdateHandoffSettingsRequest>
{
    public UpdateHandoffSettingsRequestValidator()
    {
        RuleFor(x => x.HandoffMessage).NotEmpty().MaximumLength(500).WithName("Câu chuyển nhân viên");
        RuleFor(x => x.AfterHoursMessage).NotEmpty().MaximumLength(500).WithName("Câu chuyển nhân viên ngoài giờ");
        RuleFor(x => x.TakeoverMessage).NotEmpty().MaximumLength(500).WithName("Câu khi nhân viên tiếp quản");
        RuleFor(x => x.ReturnToBotMessage).NotEmpty().MaximumLength(500).WithName("Câu khi trả lại cho bot");
        RuleFor(x => x.ResponseTime).NotEmpty().MaximumLength(50).WithName("Thời gian phản hồi");
        RuleFor(x => x.OpenTime).Must(t => HandoffTexts.TryParse(t, out _)).WithMessage("Giờ mở cửa dạng HH:mm, ví dụ 08:00.");
        RuleFor(x => x.CloseTime).Must(t => HandoffTexts.TryParse(t, out _)).WithMessage("Giờ đóng cửa dạng HH:mm, ví dụ 21:00.");
        RuleFor(x => x)
            .Must(x => !HandoffTexts.TryParse(x.OpenTime, out var open) || !HandoffTexts.TryParse(x.CloseTime, out var close) || open < close)
            .WithName("CloseTime")
            .WithMessage("Giờ đóng cửa phải sau giờ mở cửa.");
        RuleFor(x => x.WorkingDays).InclusiveBetween(0, 127).WithName("Ngày làm việc");
        RuleFor(x => x.ReminderMinutes).InclusiveBetween(5, 240).WithName("Số phút nhắc lại");
        RuleFor(x => x.CareColdHours).InclusiveBetween(1, 72).WithName("Số giờ khách im lặng");
        RuleFor(x => x.CareSendStart).Must(t => InHardWindow(t)).WithMessage("Giờ bắt đầu nhắn dạng HH:mm, trong khoảng 07:00–21:00.");
        RuleFor(x => x.CareSendEnd).Must(t => InHardWindow(t)).WithMessage("Giờ kết thúc nhắn dạng HH:mm, trong khoảng 07:00–21:00.");
        RuleFor(x => x)
            .Must(x => !HandoffTexts.TryParse(x.CareSendStart, out var a) || !HandoffTexts.TryParse(x.CareSendEnd, out var b) || a < b)
            .WithName("CareSendEnd")
            .WithMessage("Giờ kết thúc nhắn phải sau giờ bắt đầu.");
        RuleFor(x => x.TelegramChatId)
            .Must(id => TelegramChatId().IsMatch(id!))
            .WithMessage("Chat id Telegram là dãy số (nhóm thường bắt đầu bằng dấu -), hoặc @tenkenh.")
            .When(x => !string.IsNullOrWhiteSpace(x.TelegramChatId));
    }

    /// <summary>Khung giờ chủ động nhắn bị chặn cứng trong 07:00–21:00 (CareDecision).</summary>
    private static bool InHardWindow(string? value) =>
        HandoffTexts.TryParse(value ?? "", out var t) && t >= CareDecision.HardStart && t <= CareDecision.HardEnd;

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z0-9_]{5,32})$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TelegramChatId();
}

/// <param name="Code">Gõ "/ketnoi MÃ" trong nhóm Telegram đã thêm bot (hạn 10 phút, dùng 1 lần).</param>
/// <param name="BotUsername">Tên bot (không có @); null nếu không lấy được.</param>
/// <param name="AddToGroupUrl">Link mở Telegram, chọn nhóm để thêm bot và tự gửi mã.</param>
public sealed record TelegramLinkCodeResponse(string Code, DateTimeOffset ExpiresAt, string? BotUsername, string? AddToGroupUrl);

/// <summary>Cài đặt chuyển tiếp bot ↔ nhân viên (docs/FEATURE-SPECS.md mục 1). Owner sửa, staff xem.</summary>
internal static class HandoffSettingsEndpoints
{
    public static IEndpointRouteBuilder MapHandoffSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/tenant/handoff-settings").WithTags("Tenant");
        group.MapGet("", GetAsync).RequireTenantRole(TenantRole.Staff);
        group.MapPut("", UpdateAsync).RequireTenantRole(TenantRole.Owner).Validate<UpdateHandoffSettingsRequest>();
        group.MapPost("/telegram/link-code", CreateTelegramCodeAsync)
            .RequireTenantRole(TenantRole.Owner)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapDelete("/telegram", DisconnectTelegramAsync).RequireTenantRole(TenantRole.Owner);
        return app;
    }

    private static async Task<Ok<HandoffSettingsResponse>> GetAsync(
        ITenantContext tenant,
        HandoffSettingsRepository settings,
        Microsoft.Extensions.Options.IOptions<Core.Options.TelegramOptions> telegram,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(HandoffSettingsResponse.From(await settings.GetAsync(tenant.RequireTenantId(), cancellationToken), telegram.Value.IsConfigured));

    /// <summary>Mã kết nối nhóm Telegram tự phục vụ (thay cho việc tự tìm chat id). Worker nhận lệnh "/ketnoi MÃ" và lưu nhóm.</summary>
    private static async Task<Ok<TelegramLinkCodeResponse>> CreateTelegramCodeAsync(
        ITenantContext tenant,
        TelegramLinkService link,
        TelegramUpdatesClient updates,
        Microsoft.Extensions.Options.IOptions<Core.Options.TelegramOptions> telegram,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (!telegram.Value.IsConfigured)
        {
            throw new Core.Errors.ConflictException("Hệ thống chưa cài bot Telegram.", "telegram_not_configured");
        }

        var code = await link.CreateCodeAsync(tenant.RequireTenantId(), tenant.UserId, cancellationToken);
        var username = await updates.GetBotUsernameAsync(cancellationToken);
        return TypedResults.Ok(new TelegramLinkCodeResponse(
            code,
            time.GetUtcNow() + TelegramLinkService.CodeLifetime,
            username,
            username is null ? null : $"https://t.me/{username}?startgroup={code}"));
    }

    private static async Task<NoContent> DisconnectTelegramAsync(
        ITenantContext tenant,
        HandoffSettingsRepository settings,
        AuditLogRepository audit,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var s = await settings.GetOrCreateAsync(tenantId, cancellationToken);
        s.TelegramChatId = null;
        audit.Add(tenantId, tenant.UserId, "tenant.telegram_disconnected", tenantId.ToString());
        await settings.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<HandoffSettingsResponse>> UpdateAsync(
        UpdateHandoffSettingsRequest request,
        ITenantContext tenant,
        HandoffSettingsRepository settings,
        AuditLogRepository audit,
        Microsoft.Extensions.Options.IOptions<Core.Options.TelegramOptions> telegram,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.RequireTenantId();
        var s = await settings.GetOrCreateAsync(tenantId, cancellationToken);
        s.HandoffMessage = request.HandoffMessage.Trim();
        s.AfterHoursMessage = request.AfterHoursMessage.Trim();
        s.TakeoverMessageEnabled = request.TakeoverMessageEnabled;
        s.TakeoverMessage = request.TakeoverMessage.Trim();
        s.ReturnToBotMessageEnabled = request.ReturnToBotMessageEnabled;
        s.ReturnToBotMessage = request.ReturnToBotMessage.Trim();
        s.StaffSignatureEnabled = request.StaffSignatureEnabled;
        s.ResponseTime = request.ResponseTime.Trim();
        s.OpenTime = request.OpenTime;
        s.CloseTime = request.CloseTime;
        s.WorkingDays = request.WorkingDays;
        s.ReminderMinutes = request.ReminderMinutes;
        s.TelegramChatId = string.IsNullOrWhiteSpace(request.TelegramChatId) ? null : request.TelegramChatId.Trim();
        s.CareEnabled = request.CareEnabled;
        s.CareColdHours = request.CareColdHours;
        s.CareAutoSend = request.CareAutoSend;
        s.CareSendStart = request.CareSendStart;
        s.CareSendEnd = request.CareSendEnd;

        audit.Add(tenantId, tenant.UserId, "tenant.handoff_settings_updated", tenantId.ToString());
        await settings.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(HandoffSettingsResponse.From(s, telegram.Value.IsConfigured));
    }
}
