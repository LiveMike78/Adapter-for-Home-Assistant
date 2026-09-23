# syntax=docker/dockerfile:1
# Multi-arch build: use `docker buildx build --platform linux/amd64,linux/arm64 ...`
# TARGETARCH is populated automatically by buildx.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

COPY . .

# Map Docker's TARGETARCH to a .NET RID.
RUN RID=$( [ "$TARGETARCH" = "arm64" ] && echo "linux-arm64" || echo "linux-x64" ) \
    && dotnet publish src/AdapterFramework.Data.System.Host/AdapterFramework.Data.System.Host.csproj \
       -c Release -r "$RID" --self-contained true \
       -p:PublishSingleFile=true -p:PublishTrimmed=false \
       -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:${DOTNET_VERSION}
WORKDIR /app

RUN groupadd -r adapter && useradd -r -g adapter adapter
COPY --from=build /app ./
COPY ConfigurationExamples ./ConfigurationExamples
COPY Edge-Module ./Edge-Module

RUN chown -R adapter:adapter /app
USER adapter

ENV DOTNET_EnableDiagnostics=0
EXPOSE 5590

ENTRYPOINT ["./HomeAssistantAdapterHost"]
