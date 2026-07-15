# ==========================================================
# Build args
# ==========================================================
ARG CONFIG=Release
ARG DEBUG_TOOLS=false

# ==========================================================
# .NET build stage
# ==========================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG CONFIG
WORKDIR /src

COPY ArmRipper.slnx .
COPY src/ArmRipper.Core/ArmRipper.Core.csproj src/ArmRipper.Core/
COPY src/ArmRipper.Cli/ArmRipper.Cli.csproj src/ArmRipper.Cli/
COPY src/ArmRipper.WebUi/ArmRipper.WebUi.csproj src/ArmRipper.WebUi/
COPY tests/ArmRipper.Core.Tests/ArmRipper.Core.Tests.csproj tests/ArmRipper.Core.Tests/
COPY tests/ArmRipper.WebUi.Tests/ArmRipper.WebUi.Tests.csproj tests/ArmRipper.WebUi.Tests/
RUN dotnet restore

COPY . .
RUN dotnet publish src/ArmRipper.Cli/ArmRipper.Cli.csproj -c $CONFIG -o /app/cli
RUN dotnet publish src/ArmRipper.WebUi/ArmRipper.WebUi.csproj -c $CONFIG -o /app/webui

# ==========================================================
# Runtime stage
# ==========================================================
FROM automaticrippingmachine/arm-dependencies:1.7.3 AS runtime
ARG DEBUG_TOOLS

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# Install .NET runtime (HandBrakeCLI is already in the base image)
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Install diagnostic tools when DEBUG_TOOLS=true
RUN if [ "$DEBUG_TOOLS" = "true" ]; then \
        apt-get update && apt-get install -y --no-install-recommends \
            sqlite3 \
            procps \
            lsof \
            vim \
            strace \
            && rm -rf /var/lib/apt/lists/*; \
    fi

RUN curl -fsSL https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh \
    | bash /dev/stdin --channel 10.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet

ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    ASPNETCORE_URLS=http://+:8080

WORKDIR /app

COPY --from=build /app/cli /app/cli
COPY --from=build /app/webui /app/webui

RUN mkdir -p /home/arm/media/{raw,transcode,completed} /home/arm/logs /etc/arm/config /opt/arm/scripts

COPY docker-entrypoint.sh /usr/local/bin/
COPY scripts/ /opt/arm/scripts/
RUN chmod +x /opt/arm/scripts/*.sh
EXPOSE 8080
ENTRYPOINT ["docker-entrypoint.sh"]

# ==========================================================
# Debug stage — SDK + vsdbg for F5 debugging inside container
# ==========================================================
FROM automaticrippingmachine/arm-dependencies:1.7.3 AS debug
ARG DEBUG_TOOLS

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    procps \
    && rm -rf /var/lib/apt/lists/*

# Install diagnostic tools when DEBUG_TOOLS=true
RUN if [ "$DEBUG_TOOLS" = "true" ]; then \
        apt-get update && apt-get install -y --no-install-recommends \
            sqlite3 \
            lsof \
            vim \
            strace \
            && rm -rf /var/lib/apt/lists/*; \
    fi

# Install .NET SDK (not just runtime) for dotnet run/build/watch
RUN curl -fsSL https://dotnet.microsoft.com/download/dotnet/scripts/v1/dotnet-install.sh \
    | bash /dev/stdin --channel 10.0 --install-dir /usr/share/dotnet \
    && ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet

# Install vsdbg — the Visual Studio remote debugger for .NET
RUN curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l /vsdbg

ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Development

WORKDIR /src

RUN mkdir -p /home/arm/media/{raw,transcode,completed} /home/arm/logs /etc/arm/config /opt/arm/scripts

COPY docker-entrypoint.sh /usr/local/bin/
COPY scripts/ /opt/arm/scripts/
RUN chmod +x /opt/arm/scripts/*.sh

EXPOSE 8080
ENTRYPOINT ["docker-entrypoint.sh"]
CMD ["webui"]
