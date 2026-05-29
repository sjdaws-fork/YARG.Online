# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS restore
WORKDIR /src
COPY global.json ./
COPY src/YARG.Online.Lobbies/YARG.Online.Lobbies.csproj                   src/YARG.Online.Lobbies/
COPY src/YARG.Online.Lobbies.Contracts/YARG.Online.Lobbies.Contracts.csproj src/YARG.Online.Lobbies.Contracts/
COPY src/YARG.Online.Game/YARG.Online.Game.csproj                          src/YARG.Online.Game/
COPY src/YARG.Online.Game.Contracts/YARG.Online.Game.Contracts.csproj      src/YARG.Online.Game.Contracts/
RUN dotnet restore src/YARG.Online.Game/YARG.Online.Game.csproj

FROM restore AS build
ARG BUILD_CONFIGURATION=Debug
COPY . .
RUN dotnet publish src/YARG.Online.Game/YARG.Online.Game.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish --no-restore

# aspnet (not runtime) image — the game host runs Kestrel for the /metrics + /healthz
# endpoints alongside the UDP listener.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 9050/udp
EXPOSE 9091/tcp
ENTRYPOINT ["dotnet", "YARG.Online.Game.dll"]
