ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.300
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.8-noble
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY . .
ARG RUNTIME_ID=linux-x64
RUN dotnet publish src/extensions/Agentstration.Extensions.OpenDal/Agentstration.Extensions.OpenDal.csproj \
    --configuration Release --runtime $RUNTIME_ID --self-contained false --output /out

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /out .
COPY --from=build /src/LICENSE /src/NOTICE ./licenses/
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 OpenDal__RootDirectory=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "Agentstration.Extensions.OpenDal.dll"]
