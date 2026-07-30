<#
.SYNOPSIS
    Builds appPackage.zip, the Teams / Microsoft 365 Copilot app package for this agent.

.DESCRIPTION
    This agent is an Agent 365 "Agent" (not an AI teammate), so it reaches Teams the same way any
    custom engine agent does: through an Azure Bot Service registration with the Teams channel
    enabled, described by a Teams app manifest. The AI-teammate "Request instance" flow in the
    Microsoft 365 admin center does not apply here.

    BOT_ID is the application id of the Azure Bot registration. It is deliberately NOT the Agent 365
    blueprint id: Entra bars agentic applications from requesting client-credentials tokens
    (AADSTS82001), so the blueprint cannot authenticate outbound Bot Framework replies. A plain
    single-tenant Entra app owns the channel, while the blueprint continues to own Agent 365
    governance, Work IQ tools and observability.

    TEAMS_APP_ID identifies the app in the Teams catalogue. It is unrelated to any Entra identity and
    only has to stay stable across builds, otherwise each upload is treated as a brand new app
    rather than an update.

.EXAMPLE
    ./build-app-package.ps1
#>
[CmdletBinding()]
param(
    [string]$BotId = '0cf93255-7aee-4542-8df9-fc53bb8af150',
    [string]$TeamsAppId = '3f7b1c94-2d5e-4a86-9f21-8c4d0e6b7a13',
    [string]$OutputPath = "$PSScriptRoot/appPackage.zip"
)

$ErrorActionPreference = 'Stop'

if (-not $BotId) {
    throw "BotId not supplied."
}

$source = Join-Path $PSScriptRoot 'appPackage'
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "learnteamsagent-pkg-$([guid]::NewGuid())"

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
