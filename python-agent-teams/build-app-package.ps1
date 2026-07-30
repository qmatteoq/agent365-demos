<#
.SYNOPSIS
    Builds appPackage.zip, the Teams / Microsoft 365 Copilot app package for this agent.

.DESCRIPTION
    This agent reaches Teams the same way any custom engine agent does: through an Azure Bot
    Service registration with the Teams channel enabled, described by a Teams app manifest.

    BOT_ID is the application id of the Azure Bot registration. It is deliberately NOT the
    Agent 365 blueprint id: Entra bars agentic applications from requesting client-credentials
    tokens (AADSTS82001), so a blueprint cannot authenticate outbound Bot Framework replies. A
    plain single-tenant Entra app owns the channel, while the blueprint (added when the agent is
    onboarded to Agent 365) owns governance and observability.

    TEAMS_APP_ID identifies the app in the Teams catalogue. It is unrelated to any Entra identity
    and only has to stay stable across builds, otherwise each upload is treated as a brand new
    app rather than an update. It is deliberately different from the .NET Teams agent's id so
    both demo agents can be installed side by side.

.EXAMPLE
    ./build-app-package.ps1
#>
[CmdletBinding()]
param(
    [string]$BotId = 'd1fbe2ae-6c95-492f-b34a-f14451b994f5',
    [string]$TeamsAppId = 'd80a2cae-b655-487a-82be-8bf9271e1d8e',
    [string]$OutputPath = "$PSScriptRoot/appPackage.zip"
)

$ErrorActionPreference = 'Stop'

if (-not $BotId) {
    throw "BotId not supplied."
}

$source = Join-Path $PSScriptRoot 'appPackage'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "learnpyteamsagent-pkg-$([guid]::NewGuid())"

New-Item -ItemType Directory -Path $staging -Force | Out-Null

try {
    (Get-Content (Join-Path $source 'manifest.json') -Raw).
        Replace('${{BOT_ID}}', $BotId).
        Replace('${{TEAMS_APP_ID}}', $TeamsAppId) |
        Set-Content (Join-Path $staging 'manifest.json') -Encoding UTF8

    Copy-Item (Join-Path $source 'color.png') $staging
    Copy-Item (Join-Path $source 'outline.png') $staging

    if (Test-Path $OutputPath) { Remove-Item $OutputPath -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $OutputPath

    Write-Host "Built $OutputPath"
    Write-Host "  Teams app id: $TeamsAppId"
    Write-Host "  Bot id:       $BotId"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
