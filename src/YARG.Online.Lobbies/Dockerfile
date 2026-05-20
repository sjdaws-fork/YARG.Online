# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS restore
WORKDIR /src
COPY global.json ./
COPY src/YARG.Online.Lobbies/YARG.Online.Lobbies.csproj                   src/YARG.Online.Lobbies/
COPY src/YARG.Online.Lobbies.Contracts/YARG.Online.Lobbies.Contracts.csproj src/YARG.Online.Lobbies.Contracts/
COPY src/YARG.Online.Lobbies.Tests/YARG.Online.Lobbies.Tests.csproj         src/YARG.Online.Lobbies.Tests/
COPY src/YARG.Online.Game/YARG.Online.Game.csproj                          src/YARG.Online.Game/
COPY src/YARG.Online.Game.Contracts/YARG.Online.Game.Contracts.csproj      src/YARG.Online.Game.Contracts/
COPY src/YARG.Online.Game.Tests/YARG.Online.Game.Tests.csproj              src/YARG.Online.Game.Tests/
COPY src/YARG.Online.E2E.Tests/YARG.Online.E2E.Tests.csproj                src/YARG.Online.E2E.Tests/
COPY libraries/YARG.Core/YARG.Core/YARG.Core.csproj                        libraries/YARG.Core/YARG.Core/
COPY libraries/YARG.Core/Directory.Build.props                             libraries/YARG.Core/
RUN dotnet restore src/YARG.Online.Lobbies/YARG.Online.Lobbies.csproj

FROM restore AS build
COPY . .
RUN dotnet publish src/YARG.Online.Lobbies/YARG.Online.Lobbies.csproj \
    -c Debug -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5230
ENV ASPNETCORE_URLS=http://+:5230
ENTRYPOINT ["dotnet", "YARG.Online.Lobbies.dll"]
