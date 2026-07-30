using Azure.AI.OpenAI;
using Azure.Identity;
using LearnTeamsAgent.Agent;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.Extensions.AI;
using Microsoft.OpenTelemetry;
using ModelContextProtocol.Client;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

// A365 Observability — best-effort instrumentation (verify against official sample)
// The token cache turns the per-turn user token into an Observability API token. It is created
// here rather than through DI so the same instance can be handed both to the agent (which
// registers a token on every turn) and to the exporter's TokenResolver below.
var observabilityTokenCache = new AgenticTokenCache();
builder.Services.AddSingleton<IExporterTokenCache<AgenticTokenStruct>>(observabilityTokenCache);

// A365 Observability — best-effort instrumentation (verify against official sample)
// Configures the OpenTelemetry pipeline and the Agent 365 span exporter in one call. Traces are
// exported to Agent 365 (and surface in Microsoft Defender / the Microsoft Admin Center); in
// development they are mirrored to the console so a local run can be inspected immediately.
builder.UseMicrosoftOpenTelemetry(o =>
{
    o.Exporters = builder.Environment.IsDevelopment()
        ? ExportTarget.Agent365 | ExportTarget.Console
        : ExportTarget.Agent365;

    // Exporting to Agent 365 alone suppresses infrastructure instrumentation, so turn the parts
    // we care about back on: the inbound activity, the Azure OpenAI calls and the Learn MCP calls.
    o.Instrumentation.EnableAspNetCoreInstrumentation = true;
    o.Instrumentation.EnableHttpClientInstrumentation = true;
    o.Instrumentation.EnableAzureSdkInstrumentation = true;

    // On-behalf-of path: the exporter posts to /observability/, which this token authenticates.
    // UseS2SEndpoint stays at its default (false).
    o.Agent365.TokenResolver = observabilityTokenCache.GetObservabilityToken;
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
builder.Services.AddSingleton(new LearnMcpTools(learnMcpTools.Cast<AITool>().ToList()));

// A365 WorkIQ — added by add-workiq-tools skill
// Resolves the WorkIQ MCP servers listed in ToolingManifest.json and exchanges the turn's user
// token for one token per tool audience. Singleton to match AgentApplication's singleton host.
builder.Services.AddSingleton<IMcpToolServerConfigurationService, McpToolServerConfigurationService>();
builder.Services.AddSingleton<IMcpToolRegistrationService, McpToolRegistrationService>();

const string AgentInstructions = AgentDefaults.Instructions;

// A365 Observability — best-effort instrumentation (verify against official sample)
// The chat pipeline is now built explicitly rather than through AsAIAgent(), because the
// .UseOpenTelemetry() step is what makes the AI SDK emit the gen_ai inference and tool spans.
// Without it the agent still answers, but Agent 365 only ever sees a hollow parent span.
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
        ExcludeManagedIdentityCredential = builder.Environment.IsDevelopment(),
    });

    var azureClient = new AzureOpenAIClient(new Uri(aoaiEndpoint), credential);
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    return azureClient.GetChatClient(aoaiDeployment)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation(loggerFactory)
        // EnableSensitiveData puts the prompt and completion text on the span, which is what makes
        // the Defender trace view useful. Turn it off when handling regulated data.
        .UseOpenTelemetry(loggerFactory, configure: c => c.EnableSensitiveData = true)
        .Build();
});

builder.Services.AddSingleton<AIAgent>(sp =>
{
    var options = new ChatClientAgentOptions
    {
        Name = AgentDefaults.Name,
        ChatOptions = new ChatOptions
        {
            Instructions = AgentInstructions,
            Tools = learnMcpTools.Cast<AITool>().ToList(),
        },
    };

    // A365 Observability — best-effort instrumentation (verify against official sample)
    // Pin the agent id to the Agent 365 agent identity. Left unset, the SDK generates a fresh GUID
    // per agent and the exporter produces orphan identity groups it cannot authenticate.
    var observabilityAgentId = builder.Configuration["Agent365Observability:AgentId"];
    if (!string.IsNullOrEmpty(observabilityAgentId))
    {
        options.Id = observabilityAgentId;
    }

    return new ChatClientAgent(sp.GetRequiredService<IChatClient>(), options, sp.GetRequiredService<ILoggerFactory>());
});

// Conversation sessions are kept per Teams conversation so the agent has multi-turn memory.
builder.Services.AddSingleton<ConversationSessionStore>();

builder.Services.AddSingleton<IStorage, MemoryStorage>();
builder.AddAgentApplicationOptions();
builder.AddAgent<LearnAgent>();

var app = builder.Build();

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

app.MapGet("/", () => "LearnTeamsAgent is running. Channel endpoint: POST /api/messages");

app.Run();
