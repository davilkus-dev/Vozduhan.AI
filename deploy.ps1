$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'publish'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish (Join-Path $root 'Vozduhan.AI.csproj') -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o $dist
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$archive = Join-Path $env:TEMP 'vozduhan-ai-deploy.tar.gz'
if (Test-Path $archive) { Remove-Item $archive }
& tar -czf $archive -C $dist .
if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed' }
scp $archive 'dolbilkus:/tmp/vozduhan-ai-deploy.tar.gz'
if ($LASTEXITCODE -ne 0) { throw 'Upload failed' }
ssh dolbilkus 'set -e; mkdir -p /opt/vozduhan-ai; tar -xzf /tmp/vozduhan-ai-deploy.tar.gz -C /opt/vozduhan-ai; rm /tmp/vozduhan-ai-deploy.tar.gz; sudo systemctl restart vozduhan-ai.service; systemctl is-active vozduhan-ai.service'
if ($LASTEXITCODE -ne 0) { throw 'Deployment failed' }
Write-Host 'Deployment complete.'
