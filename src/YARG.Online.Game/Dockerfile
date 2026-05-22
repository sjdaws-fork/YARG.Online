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
COPY . .
RUN dotnet publish src/YARG.Online.Game/YARG.Online.Game.csproj \
    -c Debug -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 9050/udp
ENTRYPOINT ["dotnet", "YARG.Online.Game.dll"]
