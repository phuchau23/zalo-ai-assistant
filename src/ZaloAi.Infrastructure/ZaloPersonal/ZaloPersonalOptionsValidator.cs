using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ZaloAi.Core.Options;

namespace ZaloAi.Infrastructure.ZaloPersonal;

/// <summary>Chế độ giả lập chỉ được bật ở Development: lỡ cấu hình ở staging/production thì ứng dụng không khởi động.</summary>
public sealed class ZaloPersonalOptionsValidator(IHostEnvironment environment) : IValidateOptions<ZaloPersonalOptions>
{
    public ValidateOptionsResult Validate(string? name, ZaloPersonalOptions options) =>
        options.Mode == ZaloPersonalMode.Fake && !environment.IsDevelopment()
            ? ValidateOptionsResult.Fail("ZaloPersonal:Mode=Fake chỉ dùng ở môi trường Development.")
            : ValidateOptionsResult.Success;
}
