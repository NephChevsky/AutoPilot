FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# The task runner clones and commits to repositories
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY ./bin/Release/net10.0/linux-arm64/publish/ ./

ENV ASPNETCORE_HTTP_PORT=80
EXPOSE 80

ENTRYPOINT ["dotnet", "AutoPilot.WebAPI.dll"]
