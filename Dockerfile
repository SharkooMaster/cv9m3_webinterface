FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app

# Copy csproj files
COPY webinterface/webinterface.csproj ./webinterface/
COPY cross/cross.csproj ./cross/

# Restore
WORKDIR /app/webinterface
RUN dotnet restore webinterface.csproj

# Copy source
WORKDIR /app
COPY webinterface ./webinterface
COPY cross ./cross

# Remove appsettings from cross to avoid conflicts
RUN rm -f /app/cross/appsettings.json /app/cross/appsettings.*.json 2>/dev/null || true

# Publish
WORKDIR /app/webinterface
RUN dotnet publish webinterface.csproj -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build-env /app/out .

EXPOSE 5000 5001
ENTRYPOINT [ "dotnet", "webinterface.dll" ]






