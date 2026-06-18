FROM automaticrippingmachine/arm-dependencies:1.7.3

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

RUN apt-get update && apt-get install -y --no-install-recommends \
    bzip2 \
    curl \
    ca-certificates \
    git \
    gnupg \
    docker.io \
    tree \
    wget \
    && rm -rf /var/lib/apt/lists/*

COPY VERSION_MAKEMKV /tmp/VERSION_MAKEMKV
COPY install-makemkv.sh /tmp/install-makemkv.sh
COPY VERSION_NV_CODEC_HEADERS /tmp/VERSION_NV_CODEC_HEADERS
COPY install-nv-codec-headers.sh /tmp/install-nv-codec-headers.sh
COPY VERSION_HANDBRAKE /tmp/VERSION_HANDBRAKE
COPY install-handbrake.sh /tmp/install-handbrake.sh

RUN chmod +x /tmp/install-makemkv.sh \
    && /tmp/install-makemkv.sh \
    && rm -f /tmp/install-makemkv.sh /tmp/VERSION_MAKEMKV

RUN chmod +x /tmp/install-nv-codec-headers.sh /tmp/install-handbrake.sh \
    && /tmp/install-nv-codec-headers.sh \
    && /tmp/install-handbrake.sh \
    && rm -f /tmp/install-nv-codec-headers.sh /tmp/VERSION_NV_CODEC_HEADERS /tmp/install-handbrake.sh /tmp/VERSION_HANDBRAKE

RUN curl -fsSL https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh \
    | bash /dev/stdin --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    ASPNETCORE_URLS=http://+:8080

RUN dotnet --info

RUN mkdir -p /workspaces
