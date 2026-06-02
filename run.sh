#!/usr/bin/env sh
set -eu

dotnet run --project HelloWorldHttpsSite/HelloWorldHttpsSite.csproj --property:RestoreIgnoreFailedSources=true
