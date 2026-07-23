# Stage 1: Build and test the native binary
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

RUN apk add --no-cache \
    clang \
    build-base \
    zlib-dev

COPY . .

RUN dotnet restore HappyDiscard.slnx

RUN dotnet test HappyDiscard.slnx \
    --configuration Release \
    --no-restore

RUN dotnet publish HappyDiscard/HappyDiscard.csproj \
    --configuration Release \
    --runtime linux-musl-x64 \
    --self-contained true \
    /p:PublishAot=true \
    --output /app/publish

# Stage 2: Native executable only
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV Discard__ListenAddress=0.0.0.0
ENV Discard__Port=9009

EXPOSE 9009

USER $APP_UID

ENTRYPOINT ["./HappyDiscard"]
