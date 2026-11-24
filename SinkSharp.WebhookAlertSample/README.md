# Manual smoke test for SinkSharp.Sinks.Slack / SinkSharp.Sinks.MicrosoftTeams

See Program.cs. The sink(s) wired up depend on which of these two environment
variables are set — set one or both, whichever webhook(s) you want to try:

- `SLACK_WEBHOOK_URL` — e.g. `https://hooks.slack.com/services/...`
- `TEAMS_WEBHOOK_URL` — e.g. `https://....logic.azure.com/...`

## Running from a terminal

Set the variable and run `dotnet run` in the **same** shell session:

```bash
# macOS/Linux
SLACK_WEBHOOK_URL=https://hooks.slack.com/services/... dotnet run

# Windows PowerShell — two separate statements, same window
$env:SLACK_WEBHOOK_URL = "https://hooks.slack.com/services/..."
dotnet run
```

## Running from Visual Studio (F5 / Debug)

Visual Studio's debug session doesn't inherit variables from an unrelated
PowerShell window — it reads them from `Properties/launchSettings.json`
instead. That file is already set up here with empty placeholders:
fill in your webhook URL(s) there, save, then press F5 as usual.

## Treat your webhook URL like a password

Anyone with the URL can post to that channel, so keep it out of source control
(`launchSettings.json` with a real URL filled in should stay local — don't
commit it) and out of chat messages or tickets. If one is ever shared
somewhere it shouldn't be, remove it in Slack/Teams and create a fresh one.
