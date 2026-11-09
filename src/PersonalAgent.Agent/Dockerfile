# Build from the repository root:  docker build -f src/PersonalAgent.Agent/Dockerfile -t personal-agent .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PersonalAgent.Agent/PersonalAgent.Agent.csproj src/PersonalAgent.Agent/
RUN dotnet restore src/PersonalAgent.Agent/PersonalAgent.Agent.csproj
COPY src/PersonalAgent.Agent/ src/PersonalAgent.Agent/
RUN dotnet publish src/PersonalAgent.Agent/PersonalAgent.Agent.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
# Provide at runtime (never bake into the image):
#   Anthropic__ApiKey=[SECRET]   Mcp__Endpoint=https://[MCP_HOST]/mcp   Cors__AllowedOrigins__0=https://[CHAT_HOST]
ENTRYPOINT ["dotnet", "PersonalAgent.Agent.dll"]
