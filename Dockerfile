FROM node:20 AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore first (layer caching): only project + props change rarely.
COPY Directory.Build.props ./
COPY src/EasyDocs.Api/EasyDocs.Api.csproj src/EasyDocs.Api/
RUN dotnet restore src/EasyDocs.Api

COPY . .
RUN dotnet publish src/EasyDocs.Api -c Release -o /app/publish \
    && mkdir -p /app/publish/wwwroot

# Overwrite the placeholder wwwroot with the real Vite build.
COPY --from=web /web/dist /app/publish/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# libreoffice bundled now for later PDF work (M-later); large install, slow build.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EasyDocs.Api.dll"]
