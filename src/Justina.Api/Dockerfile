# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, against project files only, so a source-only change does not invalidate the package layer.
COPY Directory.Build.props ./
COPY src/Justina.Core.Domain/*.csproj                src/Justina.Core.Domain/
COPY src/Justina.Core.Application/*.csproj           src/Justina.Core.Application/
COPY src/Justina.Core.Infrastructure/*.csproj        src/Justina.Core.Infrastructure/
COPY src/Justina.Expense.Domain/*.csproj             src/Justina.Expense.Domain/
COPY src/Justina.Expense.Application/*.csproj        src/Justina.Expense.Application/
COPY src/Justina.Expense.Infrastructure/*.csproj     src/Justina.Expense.Infrastructure/
COPY src/Justina.Recruitment.Domain/*.csproj         src/Justina.Recruitment.Domain/
COPY src/Justina.Recruitment.Application/*.csproj    src/Justina.Recruitment.Application/
COPY src/Justina.Recruitment.Infrastructure/*.csproj src/Justina.Recruitment.Infrastructure/
COPY src/Justina.Api/*.csproj                        src/Justina.Api/
RUN dotnet restore src/Justina.Api/Justina.Api.csproj

COPY src/ src/
RUN dotnet publish src/Justina.Api/Justina.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libfontconfig1 / libfreetype6: PDFium and SkiaSharp need them to rasterize PDF pages.
# curl: the compose health check probes /health/live with it. The aspnet image ships neither curl nor
# wget, so without this the container never reports healthy and every service that waits on it — OpenClaw
# included — would never start.
RUN apt-get update \
    && apt-get install --no-install-recommends -y libfontconfig1 libfreetype6 curl \
    && rm -rf /var/lib/apt/lists/*

# Media is untrusted user content: it lives on its own volume, outside anything the app serves.
RUN mkdir -p /var/justina/media && chown -R app:app /var/justina

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_ENVIRONMENT=Production

EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "Justina.Api.dll"]
