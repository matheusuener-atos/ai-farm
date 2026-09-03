FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["atos.AIFarm.slnx", "./"]
COPY ["src/atos.AIFarm.Core/atos.AIFarm.Core.csproj", "src/atos.AIFarm.Core/"]
COPY ["src/atos.AIFarm.Infrastructure/atos.AIFarm.Infrastructure.csproj", "src/atos.AIFarm.Infrastructure/"]
COPY ["src/atos.AIFarm.Web/atos.AIFarm.Web.csproj", "src/atos.AIFarm.Web/"]
COPY ["src/atos.AIFarm.Agent/atos.AIFarm.Agent.csproj", "src/atos.AIFarm.Agent/"]
COPY ["tests/atos.AIFarm.UnitTests/atos.AIFarm.UnitTests.csproj", "tests/atos.AIFarm.UnitTests/"]
COPY ["tests/atos.AIFarm.IntegrationTests/atos.AIFarm.IntegrationTests.csproj", "tests/atos.AIFarm.IntegrationTests/"]
RUN dotnet restore "atos.AIFarm.slnx"

COPY . .
RUN dotnet publish "src/atos.AIFarm.Web/atos.AIFarm.Web.csproj" --configuration Release --output /app/publish --no-restore /p:UseAppHost=false
RUN dotnet publish "src/atos.AIFarm.Agent/atos.AIFarm.Agent.csproj" --configuration Release --runtime win-x64 --self-contained true --output /app/agent --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

COPY --from=build /app/agent/atos-agent.exe ./wwwroot/downloads/atos-compute-agent-win-x64.exe
ENV ASPNETCORE_URLS=http://0.0.0.0:10000
ENV DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
EXPOSE 10000

ENTRYPOINT ["dotnet", "atos.AIFarm.Web.dll"]
