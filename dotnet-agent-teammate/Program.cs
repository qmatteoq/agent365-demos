using Azure.AI.OpenAI;
using Azure.Identity;
using LearnTeammateAgent.Agent;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
// A365 WorkIQ - added by add-workiq-tools skill
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Extensions.AI;
using Microsoft.OpenTelemetry;
using ModelContextProtocol.Client;
using OpenAI.Chat;

// Two things key off "is this a local run?", and they must stay separable. The A365 Tooling SDK
// picks DevMcpTokenProvider (which demands a hand-pasted BEARER_TOKEN) when ASPNETCORE_ENVIRONMENT
// is exactly "Development", so exercising the real agentic WorkIQ path locally means running as
// Production - but the credential and console-exporter choices below still need the local answer.
// LocalRun is that separate signal: set A365_LOCAL_RUN=false only when genuinely cloud-hosted.
var isLocalRun = !string.Equals(
    Environment.GetEnvironmentVariable("A365_LOCAL_RUN"), "false", StringComparison.OrdinalIgnoreCase);

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

// A365 Observability - best-effort instrumentation (verify against official sample)
// The token cache is created up front so it can be both injected into LearnAgent (which deposits
// a token per turn) and read by the exporter's resolver below. Contrary to the skill reference,
// UseMicrosoftOpenTelemetry does NOT register IExporterTokenCache<AgenticTokenStruct> itself in
// Microsoft.OpenTelemetry 1.0.7 - without this the host fails to start.
var agenticTokenCache = new AgenticTokenCache();
builder.Services.AddSingleton<IExporterTokenCache<AgenticTokenStruct>>(agenticTokenCache);

builder.UseMicrosoftOpenTelemetry(o =>
{
    // ExportTarget.Agent365 is unconditional - traces must always reach Agent 365, including
    // during local testing, because that is what makes them visible in Defender and MAC Activity.
    // A365_LOCAL_RUN only adds the console exporter on top for local diagnosis; it never removes
    // the Agent 365 one.
    o.Exporters = isLocalRun
        ? ExportTarget.Agent365 | ExportTarget.Console
        : ExportTarget.Agent365;

    // The console metric exporter dumps every histogram bucket on a timer, drowning out the
    // spans we care about. Traces and logs still reach Agent 365.
    o.Instrumentation.EnableMetrics = false;

    // Agent365-only export suppresses infrastructure instrumentation by default. Re-enable it so
    // the outbound calls to Azure OpenAI, Entra and Learn appear in the trace.
    o.Instrumentation.EnableAspNetCoreInstrumentation = true;
    o.Instrumentation.EnableHttpClientInstrumentation = true;
    o.Instrumentation.EnableAzureSdkInstrumentation = true;

    // The exporter flushes on a background loop with no turn context, so it reads the token the
    // agent deposited for this (agent, tenant) pair.
    o.Agent365.TokenResolver = (agentId, tenantId) =>
        agenticTokenCache.GetObservabilityToken(agentId, tenantId);

    // The agentic-user path posts to /observability/ - leave UseS2SEndpoint at its default.
});

var aoaiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
var aoaiDeployment = builder.Configuration["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException("AzureOpenAI:Deployment is not configured.");
var aoaiTenantId = builder.Configuration["AzureOpenAI:TenantId"];

var learnMcpEndpoint = new Uri(builder.Configuration["LearnMcp:Endpoint"] ?? "https://learn.microsoft.com/api/mcp");

// Microsoft Learn MCP server: connect once at startup, discover the available tools,
// and hand them to the agent so it can search and fetch official Microsoft documentation.
var learnMcpTransport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = learnMcpEndpoint,
    Name = "Microsoft Learn",
    TransportMode = HttpTransportMode.StreamableHttp,
});

var learnMcpClient = await McpClient.CreateAsync(learnMcpTransport);
IList<McpClientTool> learnMcpTools = await learnMcpClient.ListToolsAsync();

builder.Services.AddSingleton(learnMcpClient);
builder.Services.AddSingleton(new LearnMcpTools(learnMcpTools));

builder.Services.AddSingleton<IChatClient>(sp =>
{
    // Pin DefaultAzureCredential to the resource's tenant, otherwise it may pick up an identity
    // from a different tenant and Azure OpenAI returns HTTP 400
    // "Tenant provided in token does not match resource token".
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        TenantId = string.IsNullOrWhiteSpace(aoaiTenantId) ? null : aoaiTenantId,
        // There is no IMDS endpoint locally; ManagedIdentityCredential can throw a fatal
        // AuthenticationFailedException that aborts the chain before the az CLI / VS credential.
        ExcludeManagedIdentityCredential = isLocalRun,
    });

    var azureClient = new AzureOpenAIClient(new Uri(aoaiEndpoint), credential);

    // A365 Observability - best-effort instrumentation (verify against official sample)
    // UseFunctionInvocation adds tool-call interception (ExecuteToolBySDK spans) and
    // UseOpenTelemetry emits the gen_ai.inference / gen_ai.tool spans that InvokeAgentScope
    // anchors as children. Without this the agent span has no LLM children in Defender.
    return azureClient.GetChatClient(aoaiDeployment)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation()
        .UseOpenTelemetry(configure: cfg => cfg.EnableSensitiveData = true)
        .Build();
});

builder.Services.AddSingleton<LearnAgentFactory>();

// A365 WorkIQ - added by add-workiq-tools skill.
// Singleton rather than the AddMcpServices() one-liner, which registers these as scoped:
// LearnAgent is a singleton, so a scoped registration would be captured and go stale.
builder.Services.AddSingleton<IMcpToolServerConfigurationService, McpToolServerConfigurationService>();
builder.Services.AddSingleton<IMcpToolRegistrationService, McpToolRegistrationService>();

// Conversation sessions are kept per Teams conversation so the agent has multi-turn memory.
builder.Services.AddSingleton<ConversationSessionStore>();

builder.Services.AddSingleton<IStorage, MemoryStorage>();

builder.AddAgentApplicationOptions();
builder.AddAgent<LearnAgent>();

var app = builder.Build();

// A liveness probe for dev tunnels and cloud hosts. Deliberately mapped outside the
// channel endpoint so a health check never needs a channel token.
app.MapGet("/api/health", () => Results.Ok(new
{
    status = "healthy",
    agent = "LearnTeammateAgent",
    timestamp = DateTimeOffset.UtcNow,
}));

// The Agents SDK channel endpoint. Teams, Microsoft 365 Copilot and the Agents Playground
// all deliver activities here.
app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken);
});

app.MapGet("/", () => "LearnTeammateAgent is running. Channel endpoint: POST /api/messages");

app.Run();
