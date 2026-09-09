# Use scripts/build-container.ps1 with an approved, locally loaded digest.
# The build context contains only reviewed prebuilt Linux runtime artifacts.
ARG DOTNET_RUNTIME_IMAGE
FROM ${DOTNET_RUNTIME_IMAGE}
WORKDIR /app
COPY api/ ./
COPY wwwroot/ ./wwwroot/
COPY worker/ ./tools/git-worker/
COPY runtime/node /usr/local/bin/node
COPY licenses/ ./licenses/
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    GitWorker__NodeExecutable=/usr/local/bin/node \
    GitWorker__ScriptPath=/app/tools/git-worker/index.mjs \
    DIAGRAMMAKER_NETWORK_POLICY_PATH=/policy/network-policy.json \
    DIAGRAMMAKER_LLM_POLICY_PATH=/policy/llm-policy.json \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DiagramMaker.Api.dll"]
