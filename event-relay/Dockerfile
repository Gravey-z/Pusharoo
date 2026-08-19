FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY shared/Pusharoo.Contracts.csproj shared/
COPY event-relay/event-relay.csproj event-relay/
RUN dotnet restore event-relay/event-relay.csproj

COPY shared/ shared/
COPY event-relay/ event-relay/
RUN dotnet publish event-relay/event-relay.csproj -c Release -o /app/publish --no-restore

FROM runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "event-relay.dll"]
