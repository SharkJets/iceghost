FROM node:22-slim@sha256:83f487e0a63425e5b4d146fb5e5be574bcbe1b7b843d3ebafdd95eaf7767a7e5 AS web
WORKDIR /web
RUN npm install -g pnpm@10
COPY web/package.json web/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY web/ ./
ARG ICEGHOST_VERSION=dev
ENV VITE_VERSION=$ICEGHOST_VERSION
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d AS build
WORKDIR /src
COPY Directory.Packages.props ./
COPY src/Iceghost.Contracts/Iceghost.Contracts.csproj src/Iceghost.Contracts/
COPY src/Iceghost.Api/Iceghost.Api.csproj src/Iceghost.Api/
RUN dotnet restore src/Iceghost.Api/Iceghost.Api.csproj
COPY src/Iceghost.Contracts/ src/Iceghost.Contracts/
COPY src/Iceghost.Api/ src/Iceghost.Api/
COPY --from=web /src/Iceghost.Api/wwwroot src/Iceghost.Api/wwwroot
RUN dotnet publish src/Iceghost.Api/Iceghost.Api.csproj -c Release --no-restore -o /app && mkdir -p /image-data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02
WORKDIR /app
COPY --from=build /app ./
COPY --from=build --chown=1654:1654 /image-data /data
USER 1654
ENV ICEGHOST_DATA=/data ASPNETCORE_HTTP_PORTS=8080 DOTNET_RUNNING_IN_CONTAINER=true
VOLUME ["/data"]
EXPOSE 8080
ENTRYPOINT ["./Iceghost.Api"]
