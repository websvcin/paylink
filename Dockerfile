# PayLink: one image, published as websvcin/paylink (Docker Hub) and ghcr.io/websvcin/paylink.
#   docker run -d -p 8080:8080 -v $(pwd)/App_Data:/app/App_Data --name paylink websvcin/paylink:latest
# Then read the setup code from `docker logs paylink` and open http://localhost:8080/setup.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# The WebSvc.PayLink.* packages are built from packages/ into a local feed until they're on nuget.org.
COPY nuget.config ./
COPY scripts/ scripts/
COPY packages/ packages/
RUN bash scripts/pack-packages.sh

COPY db/ db/
COPY src/ src/
RUN dotnet publish src/PayLink/PayLink.csproj -c Release -o /app/publish --nologo

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/App_Data && chown -R $APP_UID /app/App_Data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    PayLink__DataRoot=/app/App_Data
EXPOSE 8080
VOLUME ["/app/App_Data"]
USER $APP_UID

# bash's /dev/tcp keeps the image free of curl/wget.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "' || exit 1

ENTRYPOINT ["dotnet", "PayLink.dll"]
