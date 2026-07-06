# ── Stage 1: Build ───────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copiar csproj y restaurar dependencias
COPY *.csproj .
RUN dotnet restore

# Copiar el resto del código y compilar
COPY . .
RUN dotnet publish -c Release -o /app/publish

# ── Stage 2: Runtime ─────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Render asigna el puerto vía variable de entorno PORT
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}

ENTRYPOINT ["dotnet", "bruzz-api.dll"]
