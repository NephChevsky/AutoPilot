FROM mcr.microsoft.com/dotnet/sdk:10.0 AS sdk

FROM node:lts-slim AS node

# Use a Debian bookworm Python build so its OpenSSL ABI matches the Ubuntu-based ASP.NET runtime.
FROM python:3-slim-bookworm AS python

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# .NET 10 SDK (overlays the runtime install, same location)
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet

# Current Node LTS with npm/npx (apt's nodejs is outdated)
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx

# Latest Python 3 with pip (apt's python3 is outdated); copy the full runtime tree to keep the stdlib intact
COPY --from=python /usr/local/ /usr/local/
RUN ln -sf /usr/local/bin/python3 /usr/local/bin/python \
    && ln -sf /usr/local/bin/pip3 /usr/local/bin/pip \
    && ldconfig

# The task runner clones and commits to repositories; the lib* packages are Python's native deps missing from the aspnet image
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
        libffi8 libsqlite3-0 libexpat1 libbz2-1.0 liblzma5 libreadline8t64 \
        libncurses6 libuuid1 libzstd1 libgdbm6t64 \
    && rm -rf /var/lib/apt/lists/*

# Smoke check: fail the build if Python's native modules or pip can't load
RUN python3 -c "import ctypes, sqlite3, ssl, bz2, lzma, zlib, pyexpat, uuid" && pip --version

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
