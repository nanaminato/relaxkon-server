FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/RelaxKon.Auth/RelaxKon.Auth.csproj src/RelaxKon.Auth/
COPY src/RelaxKon.Kit/RelaxKon.Kit.csproj src/RelaxKon.Kit/
RUN dotnet restore src/RelaxKon.Auth/RelaxKon.Auth.csproj
COPY src/ src/
RUN dotnet publish src/RelaxKon.Auth/RelaxKon.Auth.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /data/keys && chown -R "$APP_UID" /data && chmod 700 /data /data/keys
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=6000
EXPOSE 6000
ENTRYPOINT ["dotnet", "RelaxKon.Auth.dll"]
