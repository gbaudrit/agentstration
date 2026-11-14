FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
COPY . .
ARG RUNTIME_ID=linux-x64
RUN dotnet publish src/Agentstration.Extensions.PostgreSql/Agentstration.Extensions.PostgreSql.csproj \
    --configuration Release --runtime $RUNTIME_ID --self-contained false --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0.8-noble
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Agentstration.Extensions.PostgreSql.dll"]
