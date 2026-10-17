FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk

FROM node:lts-slim AS node

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# .NET 10 SDK (overlays the runtime install, same location)
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet

# Current Node LTS with npm/npx (apt's nodejs is outdated)
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# The task runner clones and commits to repositories
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Commit identity for the agent (clone auth is injected at runtime from Github:Token)
RUN git config --system user.name "AutoPilot" \
    && git config --system user.email "autopilot@users.noreply.github.com" \
    && git config --system init.defaultBranch main \
    && git config --system --add safe.directory '*'

WORKDIR /app

COPY ./bin/Release/net10.0/linux-arm64/publish/ ./

ENV ASPNETCORE_HTTP_PORT=80
EXPOSE 80

# At start, configure git system-wide for github.com with the token from the Github__Token env var (never baked into the image)
ENTRYPOINT ["sh", "-c", "if [ -n \"$Github__Token\" ]; then git config --system http.https://github.com/.extraheader \"AUTHORIZATION: basic $(printf 'x-access-token:%s' \"$Github__Token\" | base64 -w0)\"; fi; exec dotnet AutoPilot.WebAPI.dll"]
