using PalitoLauncher.Core.Feedback;
using PalitoLauncher.UI.Tests.TestHelpers;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.UI.Tests;

/// <summary>Ajustes › Herramientas de desarrollo › «Ofuscar un webhook» (1.0.7).</summary>
public sealed class WebhookToolUiTests
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/AbC-dEf_123xyz";

    [Fact]
    public Task Obfuscate_CopiesThePw1ValueAndClearsTheUrl() => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        string? copied = null;
        vm.Settings.CopyText = text =>
        {
            copied = text;
            return Task.FromResult(true);
        };

        vm.Settings.WebhookToObfuscate = "  " + Webhook + "  ";
        await ((AsyncRelayCommand)vm.Settings.ObfuscateWebhookCommand).ExecuteAsync(null);

        Assert.True(vm.Settings.HasObfuscatedWebhook);
        Assert.StartsWith("pw1:", vm.Settings.ObfuscatedWebhook, StringComparison.Ordinal);
        Assert.Equal(vm.Settings.ObfuscatedWebhook, copied);
        Assert.Equal(Webhook, WebhookObfuscation.TryDecode(copied));
        Assert.Equal("", vm.Settings.WebhookToObfuscate);           // la URL no se queda a la vista
        Assert.StartsWith("Copiado", vm.Settings.ObfuscateStatus, StringComparison.Ordinal);
    });

    [Fact]
    public Task Obfuscate_ExplainsWhatIsWrong() => WpfHost.RunAsync(async () =>
    {
        using var vm = new MainViewModel();
        vm.Settings.CopyText = _ => Task.FromResult(false);
        var command = (AsyncRelayCommand)vm.Settings.ObfuscateWebhookCommand;

        await command.ExecuteAsync(null);
        Assert.StartsWith("Pega primero", vm.Settings.ObfuscateStatus, StringComparison.Ordinal);

        vm.Settings.WebhookToObfuscate = "https://ejemplo.com/hook";
        await command.ExecuteAsync(null);
        Assert.Contains("no es un webhook", vm.Settings.ObfuscateStatus, StringComparison.Ordinal);
        Assert.False(vm.Settings.HasObfuscatedWebhook);
        Assert.Equal("https://ejemplo.com/hook", vm.Settings.WebhookToObfuscate); // se deja para corregirlo

        vm.Settings.WebhookToObfuscate = Webhook;
        await command.ExecuteAsync(null);
        Assert.True(vm.Settings.HasObfuscatedWebhook);
        Assert.Contains("no se pudo copiar", vm.Settings.ObfuscateStatus, StringComparison.Ordinal);
    });
}
