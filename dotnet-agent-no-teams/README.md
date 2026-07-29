# dotnet-agent-no-teams

A minimal **Microsoft Agent Framework (.NET)** agent, hosted as a Blazor Server web app with a simple
chat UI. No Teams hosting — you chat with it directly in the browser.

The agent is specialised in the Microsoft ecosystem: it uses the official
[Microsoft Learn MCP server](https://learn.microsoft.com/api/mcp) to search and fetch authoritative
documentation, and grounds every answer in the retrieved content with source URLs.

## Stack

| Piece | Detail |
| --- | --- |
| Framework | .NET 10, Blazor Web App (Interactive Server) |
| Agent | `Microsoft.Agents.AI.OpenAI` (`AsAIAgent`) |
| Model | Azure OpenAI `gpt-4.1`, auth via `DefaultAzureCredential` |
| Tools | Microsoft Learn MCP (`microsoft_docs_search`, `microsoft_code_sample_search`, `microsoft_docs_fetch`) over streamable HTTP |

## Configuration

`appsettings.json`:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://<resource>.openai.azure.com/",
    "Deployment": "gpt-4.1",
    "TenantId": "<tenant of the Azure OpenAI resource>"
  },
  "LearnMcp": { "Endpoint": "https://learn.microsoft.com/api/mcp" }
}
```

`AzureOpenAI:TenantId` pins `DefaultAzureCredential` to the tenant that owns the Azure OpenAI
resource. Without it you may get `HTTP 400 – Tenant provided in token does not match resource token`
when your signed-in identity lives in another tenant.

You need the **Cognitive Services OpenAI User** role on the Azure OpenAI resource.

## Run

```powershell
az login --tenant <tenant of the Azure OpenAI resource>
dotnet run
```

Then open http://localhost:5140.

## Next step

This agent is intentionally free of Agent 365 plumbing. Onboarding (agent identity, blueprint,
observability) is applied afterwards with the Agent 365 tooling.
