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

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    handbrake-cli \
    ffmpeg \
    abcde \
    libdiscid0 \
    cdparanoia \
    eject \
    udev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/cli /app/cli
COPY --from=build /app/webui /app/webui

RUN mkdir -p /home/arm/media/{raw,transcode} /home/arm/logs /etc/arm/config

ENV ASPNETCORE_URLS=http://+:8080

COPY docker-entrypoint.sh /usr/local/bin/
ENTRYPOINT ["docker-entrypoint.sh"]
