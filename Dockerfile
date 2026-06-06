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

FROM automaticrippingmachine/arm-dependencies:1.7.3 AS runtime

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

RUN mkdir -p /home/arm/media/{raw,transcode,completed} /home/arm/logs /etc/arm/config

COPY docker-entrypoint.sh /usr/local/bin/
ENTRYPOINT ["docker-entrypoint.sh"]
