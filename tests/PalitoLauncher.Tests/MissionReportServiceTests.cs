using System.Net;
using System.Text.Json;
using PalitoLauncher.Core.Missions;
using PalitoLauncher.Tests.TestHelpers.F3;
using PalitoLauncher.Tests.TestHelpers.M5;
using TempDir = PalitoLauncher.Tests.TestHelpers.M5.TempDir;
using static PalitoLauncher.Tests.TestHelpers.M6.MissionSamples;

namespace PalitoLauncher.Tests;

public sealed class MissionReportServiceTests
{
    private const string Webhook = "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDEFGHIJ-0123456789";
    private const string WebhookBase = "https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyz_ABCDEFGHIJ-0123456789";

    private static MissionSet SampleSet() => Parse("""
        { "id": "pre2", "title": "Misiones del Pre 2", "missions": [
          { "id": "a", "title": "Provoca un *Vapor* en dúo", "goal": { "event": "reaction" }, "check": "¿Se vio?" },
          { "id": "b", "title": "Hospeda", "goal": { "event": "host_started" } },
          { "id": "c", "title": "Elimina 30", "goal": { "event": "enemy_killed", "count": 30, "scope": "set" } },
          { "id": "d", "title": "¿Números legibles?", "manual": true } ] }
        """);

    private static MissionSetState SampleState(MissionSet set)
    {
        var state = NewState(set);
        state.ReporterId = "abcdef";
        state.SessionsCounted = 2;
        var a = state.GetOrCreate(set.Missions[0]);
        a.State = MissionState.Done;
        a.Automatic = true;
        a.CompletedUtc = Start;
        a.CheckAnswer = false;
        a.Note = "El titular salió @everyone detrás de la nube";
        a.ErrorsNearby = 2;
        var b = state.GetOrCreate(set.Missions[1]);
        b.State = MissionState.Failed;
        b.Note = "No me deja hospedar";
        state.GetOrCreate(set.Missions[2]).Parts[0].Progress = 12;
        state.GetOrCreate(set.Missions[3]).State = MissionState.Skipped;
        return state;
    }

    private static JsonElement Embed(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("embeds")[0].Clone();
    }

    [Fact]
    public void BuildPayload_SummarizesEveryState()
    {
        var set = SampleSet();
        var payload = MissionReportService.BuildPayload(set, SampleState(set),
            new MissionReportContext { Nickname = "Palito", LauncherVersion = "1.0.6", GameVersionDisplay = "0.0.4 · Prototipo 2", NowUtc = Start });

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(0, doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("username", out _)); // se añade solo al publicar

        var embed = Embed(payload);
        Assert.Equal("🧪 Misiones del Pre 2 · Palito", embed.GetProperty("title").GetString());
        Assert.StartsWith("**1 de 4** completadas · 1 con fallos · 1 sin probar", embed.GetProperty("description").GetString());
        Assert.Equal(MissionReportService.ColorBad, embed.GetProperty("color").GetInt32());

        var fields = embed.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("value").GetString()!);
        Assert.Equal(new[] { "✅ Conseguidas (1)", "❌ Fallaron (1)", "⏳ Pendientes (1)", "➖ No las probó (1)" }, fields.Keys);

        var done = fields["✅ Conseguidas (1)"];
        Assert.Contains(@"Provoca un \*Vapor\* en dúo", done, StringComparison.Ordinal); // formato de Discord neutralizado
        Assert.Contains($"<t:{Start.ToUnixTimeSeconds()}:f>", done, StringComparison.Ordinal);
        Assert.Contains("**se vio mal**", done, StringComparison.Ordinal);
        Assert.Contains("⚠ 2 errores en esa partida", done, StringComparison.Ordinal);
        Assert.Contains("↳ El titular salió @everyone", done, StringComparison.Ordinal);
        Assert.Contains("↳ No me deja hospedar", fields["❌ Fallaron (1)"], StringComparison.Ordinal);
        Assert.Contains("Elimina 30 (12 / 30)", fields["⏳ Pendientes (1)"], StringComparison.Ordinal);

        Assert.Contains("Launcher 1.0.6 · Coop2D 0.0.4 · Prototipo 2 · pre2 · id abcdef",
            embed.GetProperty("footer").GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_AnonymousAndFinal()
    {
        var set = SampleSet();
        var state = NewState(set);
        state.ReporterId = "9f8e7d";
        foreach (var m in set.Missions) state.GetOrCreate(m).State = MissionState.Done;

        var embed = Embed(MissionReportService.BuildPayload(set, state,
            new MissionReportContext { LauncherVersion = "1.0.6", Final = true, NowUtc = Start }));

        Assert.Equal("🧪 Misiones del Pre 2 · Jugador anónimo #9f8e · Final", embed.GetProperty("title").GetString());
        Assert.Equal(MissionReportService.ColorGood, embed.GetProperty("color").GetInt32());
    }

    [Fact]
    public async Task SendAsync_PostsThenPatchesTheSameMessage()
    {
        var handler = new CapturingHttpHandler(r => r.Method == HttpMethod.Post
            ? CapturingHttpHandler.Json(HttpStatusCode.OK, """{"id":"1111222233334444","type":0}""")
            : CapturingHttpHandler.Json(HttpStatusCode.OK, """{"id":"1111222233334444"}"""));
        using var http = new HttpClient(handler);
        var service = new MissionReportService(http);

        var first = await service.SendAsync("""{"embeds":[]}""", Webhook, null, CancellationToken.None);
        Assert.Equal(MissionSendStatus.Sent, first.Status);
        Assert.Equal("1111222233334444", first.MessageId);

        var second = await service.SendAsync("""{"embeds":[]}""", Webhook, first.MessageId, CancellationToken.None);
        Assert.Equal(MissionSendStatus.Sent, second.Status);
        Assert.Equal("1111222233334444", second.MessageId);

        var requests = handler.Requests;
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(WebhookBase + "?wait=true", requests[0].Uri.AbsoluteUri);
        using (var posted = JsonDocument.Parse(requests[0].Body!))
            Assert.Equal("Coop2D · Misiones", posted.RootElement.GetProperty("username").GetString());
        Assert.Equal(HttpMethod.Patch, requests[1].Method);
        Assert.Equal(WebhookBase + "/messages/1111222233334444", requests[1].Uri.AbsoluteUri);
        Assert.DoesNotContain("username", requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendAsync_ADeletedMessageIsPostedAgain()
    {
        var handler = new CapturingHttpHandler(r => r.Method == HttpMethod.Patch
            ? CapturingHttpHandler.Status(HttpStatusCode.NotFound, """{"message":"Unknown Message","code":10008}""")
            : CapturingHttpHandler.Json(HttpStatusCode.OK, """{"id":"5555"}"""));
        using var http = new HttpClient(handler);

        var result = await new MissionReportService(http).SendAsync("{}", Webhook, "1234", CancellationToken.None);

        Assert.Equal(MissionSendStatus.Sent, result.Status);
        Assert.Equal("5555", result.MessageId);
        Assert.Equal(new[] { HttpMethod.Patch, HttpMethod.Post }, handler.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task SendAsync_KeepsTheThreadId()
    {
        var handler = new CapturingHttpHandler(_ => CapturingHttpHandler.Json(HttpStatusCode.OK, """{"id":"77"}"""));
        using var http = new HttpClient(handler);
        var service = new MissionReportService(http);

        await service.SendAsync("{}", Webhook + "?thread_id=999", null, CancellationToken.None);
        await service.SendAsync("{}", Webhook + "?thread_id=999", "77", CancellationToken.None);

        Assert.Equal(WebhookBase + "?wait=true&thread_id=999", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal(WebhookBase + "/messages/77?thread_id=999", handler.Requests[1].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(429, MissionSendStatus.RateLimited)]
    [InlineData(400, MissionSendStatus.Rejected)]
    [InlineData(401, MissionSendStatus.WebhookInvalid)]
    [InlineData(404, MissionSendStatus.WebhookInvalid)]
    [InlineData(500, MissionSendStatus.Failed)]
    public async Task SendAsync_MapsDiscordErrors(int status, MissionSendStatus expected)
    {
        var handler = new CapturingHttpHandler(_ => status == 429
            ? CapturingHttpHandler.TooManyRequests(12)
            : CapturingHttpHandler.Status((HttpStatusCode)status));
        using var http = new HttpClient(handler);

        var result = await new MissionReportService(http).SendAsync("{}", Webhook, null, CancellationToken.None);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.MessageId);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(12), result.RetryAfter);
    }

    [Fact]
    public async Task SendAsync_WithoutWebhookOrNetwork()
    {
        var handler = new CapturingHttpHandler((_, _) => throw new HttpRequestException("sin red"));
        using var http = new HttpClient(handler);
        var service = new MissionReportService(http);

        Assert.Equal(MissionSendStatus.NoWebhook, (await service.SendAsync("{}", null, null, CancellationToken.None)).Status);
        Assert.Equal(MissionSendStatus.NoWebhook, (await service.SendAsync("{}", "https://ejemplo.com", null, CancellationToken.None)).Status);
        var offline = await service.SendAsync("{}", Webhook, "42", CancellationToken.None);
        Assert.Equal(MissionSendStatus.Failed, offline.Status);
        Assert.Equal("42", offline.MessageId); // se conserva para el siguiente intento
    }

    [Fact]
    public void WebhookKey_HidesTheToken()
    {
        Assert.Equal("discord.com/123456789012345678", MissionReportService.WebhookKey(Webhook));
        Assert.Equal("discord.com/123456789012345678?thread_id=5", MissionReportService.WebhookKey(Webhook + "?thread_id=5"));
        Assert.Null(MissionReportService.WebhookKey("https://ejemplo.com"));
    }

    // ───────────────────────────── Estado en disco ─────────────────────────────

    [Fact]
    public void StateStore_RoundTripsAndRecoversFromACorruptFile()
    {
        using var temp = new TempDir();
        var store = new MissionStateStore(temp.Path);
        var set = SampleSet();
        var state = SampleState(set);
        state.ProcessedSessions.Add("s1");
        state.DiscordMessageId = "99";

        Assert.True(store.Save(state));
        var loaded = store.LoadOrCreate("pre2", Start);
        Assert.Equal("abcdef", loaded.ReporterId);
        Assert.Equal(MissionState.Done, loaded.Missions["A"].State); // sin distinguir mayúsculas
        Assert.Equal(12, loaded.Missions["c"].Parts[0].Progress);
        Assert.Equal(new[] { "s1" }, loaded.ProcessedSessions);
        Assert.Equal("99", loaded.DiscordMessageId);
        Assert.Contains("\"done\"", File.ReadAllText(store.PathFor("pre2")), StringComparison.Ordinal); // enums como texto

        File.WriteAllText(store.PathFor("pre2"), "{ roto");
        var fresh = store.LoadOrCreate("pre2", Start);
        Assert.Empty(fresh.Missions);
        Assert.Equal(Start, fresh.CreatedUtc);
        Assert.True(File.Exists(store.PathFor("pre2") + ".bad"));
    }

    [Fact]
    public void StateStore_KeepsOnlyTheMostRecentSets()
    {
        using var temp = new TempDir();
        var store = new MissionStateStore(temp.Path);
        for (var i = 0; i < MissionStateStore.MaxStoredSets + 3; i++)
        {
            store.Save(new MissionSetState { SetId = $"set-{i}", CreatedUtc = Start });
            File.SetLastWriteTimeUtc(store.PathFor($"set-{i}"), Start.UtcDateTime.AddMinutes(i));
        }

        var files = Directory.GetFiles(temp.Path, "*.json");
        Assert.Equal(MissionStateStore.MaxStoredSets, files.Length);
        Assert.True(File.Exists(store.PathFor($"set-{MissionStateStore.MaxStoredSets + 2}")));
        Assert.False(File.Exists(store.PathFor("set-0")));
    }
}
