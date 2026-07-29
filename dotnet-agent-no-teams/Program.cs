using Azure.AI.OpenAI;
using Azure.Identity;
using LearnMcpAgent.Components;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

builder.Services.AddSingleton<AIAgent>(sp =>
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

    return azureClient.GetChatClient(aoaiDeployment).AsAIAgent(
        instructions:
            "You are a Microsoft ecosystem research assistant. You specialise in answering questions about " +
            "Microsoft products and technologies - Azure, Microsoft 365, Power Platform, .NET, Windows, " +
            "Microsoft Entra, Copilot, Dynamics 365 and related services.\n" +
            "Always use the Microsoft Learn MCP tools to search and fetch authoritative documentation before " +
            "answering, even when you believe you already know the answer. Ground every factual statement in " +
            "the content you retrieved and cite the source URLs at the end of your answer.\n" +
            "If the documentation does not cover the question, say so explicitly instead of guessing. " +
            "Keep answers clear, concise and structured.",
        name: "LearnMcpAgent",
        tools: learnMcpTools.Cast<AITool>().ToList());
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
