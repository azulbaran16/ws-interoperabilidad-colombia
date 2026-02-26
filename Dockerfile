FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY InteropGateway.sln ./
COPY src/InteropGateway.Api/InteropGateway.Api.csproj src/InteropGateway.Api/
RUN dotnet restore InteropGateway.sln

COPY . .
RUN dotnet publish src/InteropGateway.Api/InteropGateway.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "InteropGateway.Api.dll"]
