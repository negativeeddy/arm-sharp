ARG HW_ACCEL=none

# ==========================================================
# HandBrake build stage: only runs when HW_ACCEL != none
# ==========================================================
FROM automaticrippingmachine/arm-dependencies:1.7.3 AS handbrake-prep
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential cmake meson ninja-build nasm python3 libnuma-dev \
    && rm -rf /var/lib/apt/lists/*

FROM handbrake-prep AS handbrake-nvidia
RUN git clone --depth 1 https://github.com/FFmpeg/nv-codec-headers.git /tmp/nv-codec-headers \
    && make -C /tmp/nv-codec-headers install && rm -rf /tmp/nv-codec-headers
WORKDIR /tmp/handbrake
RUN curl -fsSL https://github.com/HandBrake/HandBrake/releases/download/1.9.2/HandBrake-1.9.2-source.tar.bz2 \
    | tar -xj --strip-components=1 \
    && ./configure --enable-nvdec --enable-nvenc --disable-gtk \
    && cd build && make -j$(nproc) install
WORKDIR /

FROM handbrake-prep AS handbrake-intel
RUN apt-get update && apt-get install -y --no-install-recommends libmfx-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /tmp/handbrake
RUN curl -fsSL https://github.com/HandBrake/HandBrake/releases/download/1.9.2/HandBrake-1.9.2-source.tar.bz2 \
    | tar -xj --strip-components=1 \
    && ./configure --enable-qsv --disable-gtk \
    && cd build && make -j$(nproc) install
WORKDIR /

FROM handbrake-prep AS handbrake-amd
RUN git clone --depth 1 https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git /tmp/amf-headers \
    && mkdir -p /usr/include/AMF && cp -r /tmp/amf-headers/amf/public/include/* /usr/include/AMF/ \
    && rm -rf /tmp/amf-headers
WORKDIR /tmp/handbrake
RUN curl -fsSL https://github.com/HandBrake/HandBrake/releases/download/1.9.2/HandBrake-1.9.2-source.tar.bz2 \
    | tar -xj --strip-components=1 \
    && ./configure --enable-nvdec --enable-nvenc --enable-qsv --disable-gtk \
    && cd build && make -j$(nproc) install
WORKDIR /

FROM automaticrippingmachine/arm-dependencies:1.7.3 AS handbrake-none
RUN true

# ==========================================================
# .NET build stage
# ==========================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ArmRipper.slnx .
COPY src/ArmRipper.Core/ArmRipper.Core.csproj src/ArmRipper.Core/
COPY src/ArmRipper.Cli/ArmRipper.Cli.csproj src/ArmRipper.Cli/
COPY src/ArmRipper.WebUi/ArmRipper.WebUi.csproj src/ArmRipper.WebUi/
COPY tests/ArmRipper.Core.Tests/ArmRipper.Core.Tests.csproj tests/ArmRipper.Core.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/ArmRipper.Cli/ArmRipper.Cli.csproj -c Release -o /app/cli
RUN dotnet publish src/ArmRipper.WebUi/ArmRipper.WebUi.csproj -c Release -o /app/webui

# ==========================================================
# Runtime stage
# ==========================================================
FROM automaticrippingmachine/arm-dependencies:1.7.3 AS runtime

ARG HW_ACCEL

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh \
    | bash /dev/stdin --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    ASPNETCORE_URLS=http://+:8080

WORKDIR /app

COPY --from=build /app/cli /app/cli
COPY --from=build /app/webui /app/webui
COPY --from=handbrake-${HW_ACCEL} /usr/local/bin/HandBrakeCLI /usr/local/bin/HandBrakeCLI

RUN mkdir -p /home/arm/media/{raw,transcode,completed} /home/arm/logs /etc/arm/config /opt/arm/scripts

COPY docker-entrypoint.sh /usr/local/bin/
COPY scripts/ /opt/arm/scripts/
RUN chmod +x /opt/arm/scripts/*.sh
EXPOSE 8080
ENTRYPOINT ["docker-entrypoint.sh"]
