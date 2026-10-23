$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'publish'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish (Join-Path $root 'Vozduhan.AI.csproj') -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item (Join-Path $root 'deploy/vozduhan-ai.service') (Join-Path $dist 'vozduhan-ai.service')
Copy-Item (Join-Path $root 'config/bot.example.json') (Join-Path $dist 'bot.example.json')
$localConfig = Join-Path $root 'config/bot.local.json'
if (Test-Path $localConfig) {
    $previousBotConfig = [Environment]::GetEnvironmentVariable('BOT_CONFIG_PATH', 'Process')
    $env:BOT_CONFIG_PATH = $localConfig
    try {
        dotnet (Join-Path $dist 'Vozduhan.AI.dll') --validate-config
        if ($LASTEXITCODE -ne 0) { throw 'Invalid bot.local.json' }
    }
    finally {
        if ($null -eq $previousBotConfig) { Remove-Item Env:BOT_CONFIG_PATH }
        else { $env:BOT_CONFIG_PATH = $previousBotConfig }
    }
    Copy-Item $localConfig (Join-Path $dist 'bot.config.json')
}
$archive = Join-Path $env:TEMP 'vozduhan-ai-deploy.tar.gz'
if (Test-Path $archive) { Remove-Item $archive }
& tar -czf $archive -C $dist .
if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed' }
scp $archive 'dolbilkus:/tmp/vozduhan-ai-deploy.tar.gz'
if ($LASTEXITCODE -ne 0) { throw 'Upload failed' }
ssh dolbilkus 'set -e; mkdir -p /opt/vozduhan-ai; tar -xzf /tmp/vozduhan-ai-deploy.tar.gz -C /opt/vozduhan-ai; rm /tmp/vozduhan-ai-deploy.tar.gz; sudo install -m 644 /opt/vozduhan-ai/vozduhan-ai.service /etc/systemd/system/vozduhan-ai.service; if [ -f /opt/vozduhan-ai/bot.config.json ]; then sudo install -o davilkus -g davilkus -m 600 /opt/vozduhan-ai/bot.config.json /etc/vozduhan-ai.json; rm /opt/vozduhan-ai/bot.config.json; elif [ ! -f /etc/vozduhan-ai.json ]; then sudo install -o davilkus -g davilkus -m 600 /opt/vozduhan-ai/bot.example.json /etc/vozduhan-ai.json; fi; sudo systemctl daemon-reload; sudo -u davilkus env BOT_CONFIG_PATH=/etc/vozduhan-ai.json /usr/bin/dotnet /opt/vozduhan-ai/Vozduhan.AI.dll --validate-config; sudo systemctl restart vozduhan-ai.service; systemctl is-active vozduhan-ai.service'
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed' }
Write-Host 'Deployment complete.'
