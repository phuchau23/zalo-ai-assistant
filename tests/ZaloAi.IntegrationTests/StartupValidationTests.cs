using Microsoft.Extensions.Options;
using Shouldly;

namespace ZaloAi.IntegrationTests;

public sealed class StartupValidationTests
{
    private sealed class FactoryWithout(string key) : ApiFactory
    {
        protected override Dictionary<string, string?> Settings
        {
            get
            {
                var settings = base.Settings;
                settings.Remove(key);
                return settings;
            }
        }
    }

    [Theory]
    [InlineData("Security:EncryptionKey")]
    [InlineData("App:AdminUrl")]
    public void Api_refuses_to_start_when_required_setting_is_missing(string key)
    {
        using var factory = new FactoryWithout(key);

        var ex = Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        ex.Message.ShouldContain(key.Split(':')[1]);
    }
}
