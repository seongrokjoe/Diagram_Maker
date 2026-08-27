FROM node:24-bookworm-slim AS web-build
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM node:24-bookworm-slim AS git-worker-build
WORKDIR /src/tools/git-worker
COPY tools/git-worker/package.json tools/git-worker/package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts
COPY tools/git-worker/index.mjs ./

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS api-build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/DiagramMaker.Api/DiagramMaker.Api.csproj src/DiagramMaker.Api/
RUN dotnet restore src/DiagramMaker.Api/DiagramMaker.Api.csproj
COPY src/DiagramMaker.Api/ src/DiagramMaker.Api/
RUN dotnet publish src/DiagramMaker.Api/DiagramMaker.Api.csproj -c Release --no-restore -o /out

FROM node:24-bookworm-slim AS node-runtime

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY --from=api-build /out/ ./
COPY --from=web-build /src/web/dist/ ./wwwroot/
COPY --from=git-worker-build /src/tools/git-worker/ ./tools/git-worker/
ENV ASPNETCORE_URLS=http://+:8080 \
    GitWorker__ScriptPath=/app/tools/git-worker/index.mjs \
    Security__RepositoryRoot=/repositories
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "DiagramMaker.Api.dll"]
