FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY HomeApplianceManualRag.Web/HomeApplianceManualRag.Web.csproj HomeApplianceManualRag.Web/
RUN dotnet restore HomeApplianceManualRag.Web/HomeApplianceManualRag.Web.csproj

COPY HomeApplianceManualRag.Web/ HomeApplianceManualRag.Web/
RUN dotnet publish HomeApplianceManualRag.Web/HomeApplianceManualRag.Web.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

RUN apt-get update \
    && apt-get install --no-install-recommends -y libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HomeApplianceManualRag.Web.dll"]
