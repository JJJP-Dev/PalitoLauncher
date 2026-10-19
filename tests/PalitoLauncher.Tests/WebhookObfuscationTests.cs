using PalitoLauncher.Core.Feedback;
using PalitoLauncher.Core.Missions;
using PalitoLauncher.Core.Remote;

namespace PalitoLauncher.Tests;

public sealed class WebhookObfuscationTests
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz";

    /// <summary>
    /// Valor pw1 generado con una implementación independiente (Python) del formato, con nonce 01..08. Si este test falla,
    /// el formato cambió y los valores ya publicados en los JSON dejarían de funcionar: no cambies pw1, crea un pw2.
    /// </summary>
    private const string KnownVector =
        "pw1:AQIDBAUGBwgcOGKYrLiXMCrsU4v-S_Pjy6uVByG5Jd_L9vj_Tg2XFkiGdG36WKfYsKIdCDQEwwxlUTrsgNxqcseym9bVGceNPFn985_HJcldZ5tJxe-vbOo4RfR_";

    [Fact]
    public void TryDecode_KnownVector_StaysCompatible()
    {
        Assert.Equal(Webhook + "?thread_id=42", WebhookObfuscation.TryDecode(KnownVector));
    }

    [Fact]
    public void Encode_RoundTripsAndHidesTheUrl()
    {
        var a = WebhookObfuscation.Encode("  https://Discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz/  ");
        var b = WebhookObfuscation.Encode(Webhook);

        Assert.StartsWith(WebhookObfuscation.Prefix, a, StringComparison.Ordinal);
        Assert.DoesNotContain("discord", a, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webhooks", a, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AbC-dEf", a, StringComparison.Ordinal);
        Assert.NotEqual(a, b);                                     // nonce aleatorio: cada vez sale distinto…
        Assert.Equal(Webhook, WebhookObfuscation.TryDecode(a));    // …pero todos dan la misma URL (normalizada)
        Assert.Equal(Webhook, WebhookObfuscation.TryDecode(b));
        Assert.Equal(Webhook, WebhookObfuscation.TryDecode(WebhookObfuscation.Encode(a))); // rehacer un pw1 no lo anida
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es una url")]
    [InlineData("https://evil.example/api/webhooks/1/t")]
    [InlineData("http://discord.com/api/webhooks/1/t")]
    public void Encode_RejectsWhatIsNotADiscordWebhook(string url)
    {
        Assert.Throws<ArgumentException>(() => WebhookObfuscation.Encode(url));
    }

    [Fact]
    public void TryDecode_RejectsTamperedOrTruncatedValues()
    {
        var value = WebhookObfuscation.Encode(Webhook);
        var flipped = value[..^3] + (value[^3] == 'A' ? 'B' : 'A') + value[^2..];

        Assert.Null(WebhookObfuscation.TryDecode(flipped));
        Assert.Null(WebhookObfuscation.TryDecode(value[..(value.Length - 5)]));
        Assert.Null(WebhookObfuscation.TryDecode("pw1:"));
        Assert.Null(WebhookObfuscation.TryDecode("pw1:abc"));
        Assert.Null(WebhookObfuscation.TryDecode("pw1:no válido!"));
        Assert.Null(WebhookObfuscation.TryDecode(Webhook));        // no es pw1
        Assert.Null(WebhookObfuscation.TryDecode(null));
        Assert.False(SurveyProvider.IsValidDiscordWebhook(flipped));
    }

    [Fact]
    public void NormalizeDiscordWebhook_AcceptsObfuscatedValues()
    {
        var value = WebhookObfuscation.Encode(Webhook + "?thread_id=5");

        Assert.True(SurveyProvider.IsValidDiscordWebhook(value));
        Assert.True(SurveyProvider.IsValidDiscordWebhook("  " + value + "  "));
        Assert.Equal(Webhook + "?thread_id=5", SurveyProvider.NormalizeDiscordWebhook(value));
        Assert.Equal("discord.com/api/webhooks/123456789012345678/***", SurveyProvider.DescribeWebhook(value));
        Assert.True(SurveyProvider.IsValidDiscordWebhook(KnownVector));
    }

    [Fact]
    public void TheThreeJsonFiles_AcceptObfuscatedWebhooks()
    {
        var value = WebhookObfuscation.Encode(Webhook);

        var survey = SurveyProvider.Parse($$"""{ "id": "s", "discordWebhookUrl": "{{value}}", "questions": [ { "id": "a", "type": "rating", "text": "¿Qué tal?" } ] }""");
        Assert.Equal(Webhook, survey!.DiscordWebhookUrl);

        var manifest = ManifestProvider.Parse($$"""{ "bugReportWebhookUrl": "{{value}}" }""");
        Assert.Equal(Webhook, manifest!.BugReportWebhookUrl);

        var missions = MissionsProvider.Parse($$"""{ "id": "m", "discordWebhookUrl": "{{value}}", "missions": [ { "id": "a", "title": "A", "manual": true } ] }""");
        Assert.Equal(Webhook, missions!.DiscordWebhookUrl);
    }

    [Fact]
    public void IsObfuscated_OnlyLooksAtThePrefix()
    {
        Assert.True(WebhookObfuscation.IsObfuscated("pw1:xyz"));
        Assert.True(WebhookObfuscation.IsObfuscated("  PW1:xyz"));
        Assert.False(WebhookObfuscation.IsObfuscated(Webhook));
        Assert.False(WebhookObfuscation.IsObfuscated(null));
    }
}
