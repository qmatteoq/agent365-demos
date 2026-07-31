using Azure.AI.OpenAI;
using Azure.Identity;
using LearnTeamsAgent.Agent;
using LearnTeamsAgent.Agent365;
using LearnTeamsAgent.Observability;
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
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

// A365 Observability — OBO path (authMode: obo)
// The exporter posts traces with a delegated token so the trace is written under the identity of
// the person who asked, rather than under a service principal.
//
// This is the documented "custom engine using OBO" path: these turns carry no agentic identity
// (verified - agenticAppId and agenticUserId are both absent), so the Azure Bot OAuth connection
// named oboConnectionProfile is scoped to the observability API and the Bot Framework Token
// Service performs the on-behalf-of exchange itself. A single GetTurnTokenAsync returns the
// finished token.
//
// The id in the export URL is the app registration's client id to match, because the route
// authorises only when the token's azp equals the id in the URL. Probed against the live service
// with one token and three ids: agent identity 403, blueprint 403, bot app 415 (authorised, wrong
// content type). See LearnAgent.ResolveObservabilityIdentity, and
// https://learn.microsoft.com/microsoft-agent-365/developer/observability-authentication-setup
var observabilityTokenStore = new ObservabilityTokenStore();
builder.Services.AddSingleton(observabilityTokenStore);

// Registered before UseMicrosoftOpenTelemetry so this processor sits ahead of the Agent 365 export
// processor in the pipeline: OnEnd runs in registration order, and the identity has to be on the
// span before the exporter reads it. Without it the inference span - the prompt, the system
// instructions and the completion - is dropped. See BaggageBackfillProcessor.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddProcessor(new BaggageBackfillProcessor()));

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

    // Left at its default (false) on the OBO path: the delegated token is accepted by
    // /observability/, not the /observabilityService/ route the S2S token targets.

    // The exporter flushes on a background loop with no user context, so the token is deposited
    // by the turn and read back here.
    o.Agent365.TokenResolver = (agentId, tenantId) =>
        Task.FromResult(observabilityTokenStore.Get(agentId, tenantId));
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

// The SDK registration above is kept so the tooling extension stays wired, but the agent resolves
// its WorkIQ tools through the provider below instead. The SDK first calls the tooling gateway at
// /agents/v2/{agentId}/mcpServers to discover the servers, and that route is failing service side
// with a 500 for any agent id. The servers themselves respond normally, so the provider skips
// discovery and connects to the urls already present in ToolingManifest.json.
builder.Services.AddSingleton<WorkIqTokenService>();
builder.Services.AddSingleton<WorkIqToolProvider>();

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
        // Managed identity only exists on Azure infrastructure. Off Azure there is no IMDS endpoint
        // and ManagedIdentityCredential throws a fatal AuthenticationFailedException that aborts the
        // chain before the az CLI / Visual Studio credential is ever tried. The ASPNETCORE_ENVIRONMENT
        // name is the wrong signal for this, because the agent runs in Production locally whenever it
        // is exposed to Teams through a dev tunnel. Opt in explicitly when deploying to Azure.
        ExcludeManagedIdentityCredential = !builder.Configuration.GetValue("AzureOpenAI:UseManagedIdentity", false),
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
    // Pin the agent id so it matches the id the exporter puts in the export URL and looks the
    // token up by. Left unset, the SDK generates a fresh GUID per agent and the exporter produces
    // orphan identity groups it cannot authenticate. For a custom engine agent that id is the app
    // registration's client id - see LearnAgent.ResolveObservabilityIdentity.
    var observabilityAgentId = builder.Configuration["Connections:BotConnection:Settings:ClientId"];
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
