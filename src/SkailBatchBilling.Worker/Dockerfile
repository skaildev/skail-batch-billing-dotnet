# Build context: repository root.
#   podman build -f src/SkailBatchBilling.Worker/Dockerfile -t batch-billing-worker .
# dotnet publish runs in the SDK image; the final image only needs the .NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailBatchBilling.Contracts/SkailBatchBilling.Contracts.csproj src/SkailBatchBilling.Contracts/
COPY src/SkailBatchBilling.Worker/SkailBatchBilling.Worker.csproj src/SkailBatchBilling.Worker/
RUN dotnet restore src/SkailBatchBilling.Worker/SkailBatchBilling.Worker.csproj
COPY src/SkailBatchBilling.Contracts src/SkailBatchBilling.Contracts
COPY src/SkailBatchBilling.Worker src/SkailBatchBilling.Worker
RUN dotnet publish src/SkailBatchBilling.Worker/SkailBatchBilling.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SkailBatchBilling.Worker.dll"]
