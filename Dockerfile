# syntax=docker/dockerfile:1
#
# BuildChatBot in a container: Android test builds only (/test_build_android).
# The iOS release commands need macOS + Xcode; inside this Linux image they refuse to run.
#
# Android build tools (aapt2 from Maven, the NDK) ship for x86_64 Linux only, so build and
# run this image as linux/amd64 (docker-compose.yml pins it; on Apple Silicon Docker Desktop
# emulates it). See docker/README.md for the full guide.

ARG DOTNET_VERSION=8.0

# ---------- 1. Build the bot ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

COPY src/BuildChatBot.csproj ./
RUN RID="linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)" \
 && dotnet restore -r "$RID"

COPY src/ ./
RUN RID="linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)" \
 && dotnet publish BuildChatBot.csproj -c Release -r "$RID" --self-contained false --no-restore -o /out

# ---------- 2. Runtime: .NET + JDK 17 + Flutter + Android SDK ----------
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION}-bookworm-slim

# Match the Flutter version the app repo is built with on the host (`flutter --version`).
ARG FLUTTER_VERSION=3.41.1
ARG ANDROID_CMDLINE_TOOLS=11076708
# Flutter's defaults for compileSdk / ndkVersion (FlutterExtension.kt). Gradle downloads
# anything else the app needs on the first build. Set ANDROID_NDK= to skip the NDK.
ARG ANDROID_PLATFORM=android-36
ARG ANDROID_BUILD_TOOLS=35.0.0
ARG ANDROID_NDK=28.2.13676358
# Use your host user's ids on Linux, so files the bot writes into the mounted repo stay yours.
ARG BOT_UID=1000
ARG BOT_GID=1000

ENV DEBIAN_FRONTEND=noninteractive \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    JAVA_HOME=/opt/java \
    ANDROID_SDK_ROOT=/opt/android-sdk \
    ANDROID_HOME=/opt/android-sdk \
    FLUTTER_ROOT=/opt/flutter \
    PUB_CACHE=/home/bot/.pub-cache \
    GRADLE_USER_HOME=/home/bot/.gradle \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV PATH=/opt/flutter/bin:/opt/android-sdk/cmdline-tools/latest/bin:/opt/android-sdk/platform-tools:/opt/java/bin:$PATH

# The man1 folder is missing on slim images and the JDK postinst fails without it.
RUN mkdir -p /usr/share/man/man1 \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates curl git openssh-client unzip xz-utils zip tini \
        openjdk-17-jdk-headless \
 && rm -rf /var/lib/apt/lists/* \
 && ln -s "$(dirname "$(dirname "$(readlink -f "$(command -v javac)")")")" /opt/java \
 && git config --system --add safe.directory '*'

# -o: macOS hosts use gid 20, which Debian already has (dialout).
RUN groupadd -o -g "${BOT_GID}" bot \
 && useradd -o -m -u "${BOT_UID}" -g "${BOT_GID}" -s /bin/bash bot \
 && mkdir -p /opt/flutter /opt/android-sdk /data /repo /home/bot/.gradle /home/bot/.pub-cache /home/bot/.ssh \
 && chmod 700 /home/bot/.ssh \
 && chown -R bot:bot /opt/flutter /opt/android-sdk /data /repo /home/bot

# Flutter warns on every command when it runs as root, so the image runs as `bot`.
USER bot

RUN curl -fsSL "https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_${FLUTTER_VERSION}-stable.tar.xz" \
      | tar -xJ -C /opt \
 && flutter config --no-analytics \
 && dart --disable-analytics \
 && flutter precache --android \
 && flutter --version

RUN curl -fsSL -o /tmp/cmdline.zip "https://dl.google.com/android/repository/commandlinetools-linux-${ANDROID_CMDLINE_TOOLS}_latest.zip" \
 && unzip -q /tmp/cmdline.zip -d /tmp/cmdline \
 && mkdir -p /opt/android-sdk/cmdline-tools \
 && mv /tmp/cmdline/cmdline-tools /opt/android-sdk/cmdline-tools/latest \
 && rm -rf /tmp/cmdline /tmp/cmdline.zip \
 && (yes || true) | sdkmanager --licenses > /dev/null \
 && sdkmanager "platform-tools" "platforms;${ANDROID_PLATFORM}" "build-tools;${ANDROID_BUILD_TOOLS}" \
        ${ANDROID_NDK:+"ndk;${ANDROID_NDK}"} \
 && flutter config --android-sdk /opt/android-sdk

COPY --from=build --chown=bot:bot /out /app
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/entrypoint.sh

# logs/, cache/apks/, tmp/ and the WTelegram session live here (mount a volume on it).
WORKDIR /data

# tini reaps the Gradle/Kotlin daemons the builds leave behind and forwards SIGTERM.
ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint.sh"]
