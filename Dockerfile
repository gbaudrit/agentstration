ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Agentstration.Web/Agentstration.Web.csproj -c Release -o /app/publish

FROM ${RUNTIME_IMAGE}
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /src/deploy/bootstrap/profiles /app/bootstrap/profiles
COPY --from=build /src/LICENSE /src/NOTICE ./licenses/
ENV ASPNETCORE_URLS=http://+:8080
ENV Data__Directory=/data
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "Agentstration.Web.dll"]
