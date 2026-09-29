using System.Net;
using System.Text;
using AIGovernanceGateway.Configuration;
using AIGovernanceGateway.Data;
using AIGovernanceGateway.Guardrails;
using AIGovernanceGateway.Models;
using AIGovernanceGateway.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace AIGovernanceGateway.Tests;

public sealed class TelemetryGuardrailTests
{
    [Fact]
    public void Sanitizer_redacts_credentials_but_preserves_normal_payload_content()
    {
        var options = new LoggingOptions
        {
            RedactedJsonFields = ["tenantSecret"],
            RedactedHeaders = ["X-Custom-Secret"]
        };
        var json = Encoding.UTF8.GetBytes(
            """{"prompt":"summarize this","password":"do-not-log","tenantSecret":"hidden","nested":{"api_key":"secret"}}""");

        var sanitized = TelemetrySanitizer.SanitizePayloadText(json, "application/json", options);

        Assert.Contains("summarize this", sanitized);
        Assert.DoesNotContain("do-not-log", sanitized);
        Assert.DoesNotContain("hidden", sanitized);
        Assert.DoesNotContain("\"secret\"", sanitized);

        var headers = TelemetrySanitizer.SanitizeHeaders(
        [
            new KeyValuePair<string, StringValues>("Authorization", "Bearer token"),
            new KeyValuePair<string, StringValues>("X-Custom-Secret", "value"),
            new KeyValuePair<string, StringValues>("X-Request-Name", "visible")
        ], options);

        Assert.Equal("[REDACTED]", headers["Authorization"]);
        Assert.Equal("[REDACTED]", headers["X-Custom-Secret"]);
        Assert.Equal("visible", headers["X-Request-Name"]);
    }

    [Fact]
    public async Task Guardrail_blocks_when_evaluator_returns_block_action()
    {
        await using var db = CreateDb();
        var route = AddGuardrailRoute(db);
        await db.SaveChangesAsync();

        var handler = new StaticModelHandler(
            """{"risk_score":92,"action":"block","reason":"Policy violation","categories":["restricted"]}""");
        var telemetry = CreateTelemetry();
        var service = new GuardrailService(
            db,
            new ModelRouterService(db, null!, new ClientFactory(new HttpClient(handler))),
            telemetry,
            Options.Create(new GuardrailsOptions
            {
                Enabled = true,
                Model = route.PublicName,
                PolicyPrompt = "Block restricted content.",
                BlockThreshold = 70
            }),
            Options.Create(new LoggingOptions()));

        var decision = await service.EvaluateAsync(
            GuardrailStage.Input,
            Encoding.UTF8.GetBytes("""{"prompt":"restricted sample"}"""),
            "application/json",
            new { path = "/v1/chat/completions" },
            default);

        Assert.False(decision.Allowed);
        Assert.Equal(92, decision.RiskScore);
        Assert.Equal("block", decision.Action);
        Assert.Contains("restricted", decision.Categories);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Guardrail_threshold_can_block_an_evaluator_allow_decision()
    {
        await using var db = CreateDb();
        var route = AddGuardrailRoute(db);
        await db.SaveChangesAsync();

        var handler = new StaticModelHandler(
            """{"risk_score":75,"action":"allow","reason":"Borderline","categories":["review"]}""");
        var service = new GuardrailService(
            db,
            new ModelRouterService(db, null!, new ClientFactory(new HttpClient(handler))),
            CreateTelemetry(),
            Options.Create(new GuardrailsOptions
            {
                Enabled = true,
                Model = route.PublicName,
                BlockThreshold = 70
            }),
            Options.Create(new LoggingOptions()));

        var decision = await service.EvaluateAsync(
            GuardrailStage.Output,
            Encoding.UTF8.GetBytes("candidate response"),
            "text/plain",
            null,
            default);

        Assert.False(decision.Allowed);
        Assert.Equal("block", decision.Action);
        Assert.Equal(75, decision.RiskScore);
    }

    [Fact]
    public async Task Guardrail_failure_mode_controls_evaluator_failures()
    {
        await using var db = CreateDb();

        var allowService = new GuardrailService(
            db,
            new ModelRouterService(db, null!, new ClientFactory(new HttpClient(new StaticModelHandler("{}")))),
            CreateTelemetry(),
            Options.Create(new GuardrailsOptions
            {
                Enabled = true,
                Model = "missing",
                FailureMode = GuardrailFailureMode.Allow
            }),
            Options.Create(new LoggingOptions()));

        var blockService = new GuardrailService(
            db,
            new ModelRouterService(db, null!, new ClientFactory(new HttpClient(new StaticModelHandler("{}")))),
            CreateTelemetry(),
            Options.Create(new GuardrailsOptions
            {
                Enabled = true,
                Model = "missing",
                FailureMode = GuardrailFailureMode.Block
            }),
            Options.Create(new LoggingOptions()));

        var allowed = await allowService.EvaluateAsync(
            GuardrailStage.Input,
            Encoding.UTF8.GetBytes("content"),
            "text/plain",
            null,
            default);
        var blocked = await blockService.EvaluateAsync(
            GuardrailStage.Input,
            Encoding.UTF8.GetBytes("content"),
            "text/plain",
            null,
            default);

        Assert.True(allowed.Allowed);
        Assert.True(allowed.EvaluationFailed);
        Assert.False(blocked.Allowed);
        Assert.True(blocked.EvaluationFailed);
    }

    private static GovernanceDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<GovernanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("n"))
            .Options;
        return new GovernanceDbContext(options);
    }

    private static ModelRoute AddGuardrailRoute(GovernanceDbContext db)
    {
        var provider = new ModelProvider
        {
            Name = "Guardrail Provider",
            Slug = "guardrail-provider",
            Kind = ModelProviderKind.OpenAiCompatible,
            BaseEndpoint = "https://guardrail.example"
        };
        var route = new ModelRoute
        {
            ProviderId = provider.Id,
            Provider = provider,
            PublicName = "guardrail-model",
            DownstreamModel = "guardrail-small"
        };
        db.AddRange(provider, route);
        return route;
    }

    private static GatewayTelemetry CreateTelemetry() =>
        new(
            NullLogger<GatewayTelemetry>.Instance,
            Options.Create(new LoggingOptions { TelemetryEnabled = true }),
            new HttpContextAccessor());

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticModelHandler(string decision) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        message = new { role = "assistant", content = decision },
                        finish_reason = "stop"
                    }
                },
                usage = new { prompt_tokens = 10, completion_tokens = 4 }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}