FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
COPY . .
ARG SERVICE
RUN dotnet publish src/${SERVICE}/${SERVICE}.csproj -c Release -o /app /p:RestoreLockedMode=true /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ARG SERVICE
ENV SERVICE_DLL=${SERVICE}.dll
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\" \"$@\"", "--"]
