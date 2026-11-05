# Build context: repository root.
#   podman build -f src/SkailBatchBilling.Web/Dockerfile -t batch-billing-web .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailBatchBilling.Contracts/SkailBatchBilling.Contracts.csproj src/SkailBatchBilling.Contracts/
COPY src/SkailBatchBilling.Web/SkailBatchBilling.Web.csproj src/SkailBatchBilling.Web/
RUN dotnet restore src/SkailBatchBilling.Web/SkailBatchBilling.Web.csproj
COPY src/SkailBatchBilling.Contracts src/SkailBatchBilling.Contracts
COPY src/SkailBatchBilling.Web src/SkailBatchBilling.Web
RUN dotnet publish src/SkailBatchBilling.Web/SkailBatchBilling.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SkailBatchBilling.Web.dll"]
