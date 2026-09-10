# syntax=docker/dockerfile:1

# --------------------------------------------------------------------------
# Build
# --------------------------------------------------------------------------
# Project files are copied on their own first so that `restore` becomes its own
# layer. Editing source then rebuilds without re-downloading every package,
# which is the difference between a ten-second and a two-minute rebuild.
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/Ledger.Domain/Ledger.Domain.csproj src/Ledger.Domain/
COPY src/Ledger.Application/Ledger.Application.csproj src/Ledger.Application/
COPY src/Ledger.Infrastructure/Ledger.Infrastructure.csproj src/Ledger.Infrastructure/
COPY src/Ledger.Api/Ledger.Api.csproj src/Ledger.Api/

RUN dotnet restore src/Ledger.Api/Ledger.Api.csproj

COPY src/ src/

# The same warnings-as-errors bar the solution builds under. An image that
# compiles code the CI would reject is worse than no image.
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        -warnaserror

# --------------------------------------------------------------------------
# Runtime
# --------------------------------------------------------------------------
# The ASP.NET runtime image, not the SDK: no compiler, no NuGet cache, no source.
# Anything that is not needed to run the service is one less thing to patch and
# one less thing an attacker can use.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

# Alpine ships without ICU. The service formats money and timestamps with the
# invariant culture deliberately, but leaving globalization broken means any
# future culture-aware code fails at runtime rather than at review.
RUN apk add --no-cache icu-libs

WORKDIR /app
COPY --from=build /app/publish .

# A named, unprivileged account rather than root. Created explicitly instead of
# relying on the base image's own user, so the image behaves the same if that
# ever changes.
RUN addgroup -S ledger && adduser -S -G ledger -H -s /sbin/nologin ledger \
    && chown -R ledger:ledger /app
USER ledger

# Above 1024, because an unprivileged process cannot bind a privileged port.
# Overridable: the port is configuration, not a property of the image.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

# No connection strings, no credentials, no environment-specific values are baked
# in. Everything a deployment needs is supplied to the container at run time.
ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
