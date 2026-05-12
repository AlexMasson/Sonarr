# syntax=docker/dockerfile:1
# Local build of Sonarr fork (feature/llm-prioritization).
# Build context = root of this repo.
# Used by /home/alexandre/docker/media/docker-compose.yaml

# --- Stage 1: Build frontend ---
FROM node:20-alpine AS frontend

WORKDIR /src
# Copier seulement les manifests d'abord → yarn install mis en cache tant que package.json/yarn.lock ne changent pas
COPY package.json yarn.lock /src/
RUN yarn install --frozen-lockfile

# Puis le reste du source (invalidé à chaque commit, mais yarn install reste caché)
COPY . /src
RUN yarn build

# --- Stage 2: Build backend ---
FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS builder

COPY --from=frontend /src /src

WORKDIR /src/src

# Remove global.json version lock so dotnet uses the available SDK
RUN rm -f /src/global.json

RUN --mount=type=cache,id=sonarr-nuget,target=/root/.nuget/packages \
    dotnet restore --disable-parallel Sonarr.sln && \
    dotnet msbuild Sonarr.sln \
      -p:Configuration=Release \
      -p:RuntimeIdentifiers=linux-musl-x64 \
      -t:PublishAllRids \
      /p:TreatWarningsAsErrors=false && \
    mkdir /build && \
    cp -r /src/_output/net6.0/linux-musl-x64/publish/* /build/ && \
    cp -r /src/_output/UI /build/UI && \
    cp /usr/share/dotnet/host/fxr/6*/libhostfxr.so /build/ && \
    cp /usr/share/dotnet/shared/Microsoft.NETCore.App/6*/libcoreclr.so /build/ && \
    cp /usr/share/dotnet/shared/Microsoft.NETCore.App/6*/libhostpolicy.so /build/ && \
    (cp /usr/share/dotnet/shared/Microsoft.NETCore.App/6*/lib*.so /build/ 2>/dev/null || true)

# --- Stage 3: Runtime image (same as upstream linuxserver) ---
FROM ghcr.io/linuxserver/baseimage-alpine:3.20

ARG BUILD_DATE
ARG VERSION
LABEL build_version="Custom LLM-prioritization build:- ${VERSION} Build-date:- ${BUILD_DATE}"
LABEL maintainer="AlexMasson"

ENV XDG_CONFIG_HOME="/config/xdg" \
    SONARR_CHANNEL="v4-stable" \
    SONARR_BRANCH="main" \
    COMPlus_EnableDiagnostics=0 \
    TMPDIR=/run/sonarr-temp

RUN \
  echo "**** install packages ****" && \
  apk add --no-cache \
    ffmpeg \
    icu-libs \
    sqlite-libs \
    xmlstarlet && \
  mkdir -p /app/sonarr/bin

COPY --from=builder /build/ /app/sonarr/bin/

# Ensure embedded ffprobe/ffmpeg binaries are executable for non-root user
RUN chmod a+rx /app/sonarr/bin/ffprobe /app/sonarr/bin/ffmpeg 2>/dev/null || true

RUN \
  echo -e "UpdateMethod=docker\nBranch=feature/llm-prioritization\nPackageVersion=${VERSION:-LocalBuild}\nPackageAuthor=AlexMasson (fork)" > /app/sonarr/package_info && \
  printf "Custom build version: ${VERSION}\nBuild-date: ${BUILD_DATE}" > /build_version

# linuxserver s6-overlay services (copied from docker-sonarr/root/)
COPY docker-root/ /

EXPOSE 8989

VOLUME /config
