# syntax=docker/dockerfile:1
# Multi-stage build for the Weir host. Build the whole app (including the Blazor WASM admin, which
# the host serves as static assets) on the .NET 10 SDK image, then run on the aspnet:10.0 runtime.
# Build context is the repository root:  docker build -f build/Dockerfile -t weir .

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the build configuration first so restore can be cached across source-only changes.
COPY Directory.Build.props Directory.Packages.props global.json Weir.slnx ./
COPY src ./src

RUN dotnet restore src/Weir.Host/Weir.Host.csproj
RUN dotnet publish src/Weir.Host/Weir.Host.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Configure at runtime via environment, e.g.:
#   -e Weir__DataConnections__default__ConnectionString="Server=...;Database=...;"
#   -e Weir__Admin__Username=admin -e Weir__Admin__Password=... -e Weir__Jwt__SigningKey=...
ENTRYPOINT ["dotnet", "Weir.Host.dll"]
