# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ConsoleATM.sln ./
COPY src/AtmMachine.WebUI/AtmMachine.WebUI.csproj src/AtmMachine.WebUI/
RUN dotnet restore src/AtmMachine.WebUI/AtmMachine.WebUI.csproj

COPY . .
RUN dotnet publish src/AtmMachine.WebUI/AtmMachine.WebUI.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./
COPY render-entrypoint.sh ./render-entrypoint.sh
RUN chmod +x ./render-entrypoint.sh

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["./render-entrypoint.sh"]
