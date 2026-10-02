using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using ZaloAi.Infrastructure.Logging;

namespace ZaloAi.UnitTests.Logging;

public sealed class SensitiveDataRedactorTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static string LogAndRender(Action<ILogger> write)
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .Enrich.With<SensitiveDataRedactor>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        write(logger);

        var e = sink.Events.ShouldHaveSingleItem();
        return e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
            + " | " + string.Join(", ", e.Properties.Select(p => $"{p.Key}={p.Value}"));
    }

    [Theory]
    [InlineData("AccessToken")]
    [InlineData("refresh_token")]
    [InlineData("Password")]
    [InlineData("ApiKey")]
    [InlineData("PhoneNumber")]
    [InlineData("Content")]
    public void Sensitive_property_name_is_masked(string name)
    {
        var output = LogAndRender(l => l.Information("value {" + name + "}", "SUPER-SECRET-VALUE"));

        output.ShouldNotContain("SUPER-SECRET-VALUE");
        output.ShouldContain(SensitiveDataRedactor.Mask);
    }

    [Theory]
    [InlineData("0912345678")]
    [InlineData("0912 345 678")]
    [InlineData("091.234.5678")]
    [InlineData("+84912345678")]
    [InlineData("khach@example.com")]
    public void Phone_and_email_inside_normal_values_are_masked(string pii)
    {
        var output = LogAndRender(l => l.Information("Ghi chú {Note}", $"khách để lại {pii} nhờ gọi lại"));

        output.ShouldNotContain(pii);
        output.ShouldContain("khách để lại *** nhờ gọi lại");
    }

    [Fact]
    public void Nested_object_sensitive_fields_are_masked()
    {
        var request = new { OaId = "oa-123", AccessToken = "tok-xyz", Customer = new { Phone = "0987654321", Name = "An" } };

        var output = LogAndRender(l => l.Information("Gọi Zalo {@Request}", request));

        output.ShouldContain("oa-123");
        output.ShouldNotContain("tok-xyz");
        output.ShouldNotContain("0987654321");
    }

    [Fact]
    public void Normal_values_are_unchanged()
    {
        var output = LogAndRender(l => l.Information("Job {JobId} xong sau {Ms} ms, ContentType {ContentType}", "job-42", 120, "application/json"));

        output.ShouldContain("job-42");
        output.ShouldContain("120");
        output.ShouldContain("application/json");
    }

    [Fact]
    public void Numbers_that_are_not_phone_numbers_are_unchanged()
    {
        var output = LogAndRender(l => l.Information("Tổng {Amount}", "Giá 1500000 đồng, mã đơn 2026100212345"));

        output.ShouldContain("1500000");
        output.ShouldContain("2026100212345");
    }
}
