using Azure.AI.OpenAI;
using Azure.Identity;
using LearnMcpAgent.Agent365;
using LearnMcpAgent.Components;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.OpenTelemetry;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ModelContextProtocol.Client;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------------------
// Agent 365 identity configuration
// ---------------------------------------------------------------------------
var obsConfig = builder.Configuration.GetSection("Agent365Observability");

var a365Config = new A365Config
{
    TenantId = obsConfig["TenantId"] ?? string.Empty,
    BlueprintClientId = obsConfig["AgentBlueprintId"] ?? string.Empty,
    AgentIdentityClientId = obsConfig["AgentId"] ?? string.Empty,
    AgentName = obsConfig["AgentName"] ?? "LearnMcpAgent",
    // Real secret comes from user-secrets / environment; appsettings only carries a placeholder.
    BlueprintClientSecret = builder.Configuration["Agent365:BlueprintClientSecret"] ?? string.Empty,
};

builder.Services.AddSingleton(a365Config);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AgentOboTokenService>();

var observabilityTokenStore = new ObservabilityTokenStore();
builder.Services.AddSingleton(observabilityTokenStore);

// ---------------------------------------------------------------------------
// Entra ID sign-in. An agent blueprint cannot run interactive /authorize flows, so the web app
// signs users in with its own client app registration and requests the blueprint's
// access_agent_as_user scope. The resulting token is the user assertion for the agent OBO chain.
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
    .EnableTokenAcquisitionToCallDownstreamApi([a365Config.AgentUserScope])
    .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddMicrosoftIdentityConsentHandler();
builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();

// ---------------------------------------------------------------------------
// A365 observability. The exporter flushes on a background loop with no user context, so the
// per-user token is deposited in ObservabilityTokenStore by the chat page and read back here.
// ---------------------------------------------------------------------------
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

    // The console metric exporter dumps every histogram bucket on a timer, which drowns out the
    // spans we actually care about. Traces and logs still reach Agent 365.
    o.Instrumentation.EnableMetrics = false;

    // OBO path posts to /observability/ — leave UseS2SEndpoint at its default (false).
    o.Agent365.TokenResolver = (agentId, tenantId) =>
        Task.FromResult(observabilityTokenStore.Get(agentId, tenantId));
});

// ---------------------------------------------------------------------------
// Microsoft Learn MCP server — connect once at startup and expose its tools to the agent.
// ---------------------------------------------------------------------------
var learnMcpEndpoint = new Uri(builder.Configuration["LearnMcp:Endpoint"] ?? "https://learn.microsoft.com/api/mcp");

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

// A365 WorkIQ — added by add-workiq-tools skill.
// The MCP servers themselves come from ToolingManifest.json (a365 develop add-mcp-servers).
builder.Services.AddSingleton<WorkIqToolProvider>();
builder.Services.AddSingleton<LearnAgentFactory>();

// ---------------------------------------------------------------------------
// Chat client + agent
// ---------------------------------------------------------------------------
var aoaiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
var aoaiDeployment = builder.Configuration["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException("AzureOpenAI:Deployment is not configured.");
var aoaiTenantId = builder.Configuration["AzureOpenAI:TenantId"];

builder.Services.AddSingleton<IChatClient>(sp =>
{
    // Pin DefaultAzureCredential to the tenant that owns the Azure OpenAI resource, otherwise it
    // may present a token from another tenant and the service returns HTTP 400
    // "Tenant provided in token does not match resource token".
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        TenantId = string.IsNullOrWhiteSpace(aoaiTenantId) ? null : aoaiTenantId,
        // No IMDS endpoint locally; ManagedIdentityCredential can abort the whole chain.
        ExcludeManagedIdentityCredential = builder.Environment.IsDevelopment(),
    });

    var azureClient = new AzureOpenAIClient(new Uri(aoaiEndpoint), credential);

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


var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
