FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Directory.Packages.props ../
COPY CamposDev.Tuya/CamposDev.Tuya.csproj CamposDev.Tuya/
COPY MqttThSensorWorker/MqttThSensorWorker.csproj MqttThSensorWorker/
RUN dotnet restore MqttThSensorWorker/MqttThSensorWorker.csproj

COPY CamposDev.Tuya/ CamposDev.Tuya/
COPY MqttThSensorWorker/ MqttThSensorWorker/
RUN dotnet publish MqttThSensorWorker/MqttThSensorWorker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN groupadd --gid 1000 appgroup && \
    useradd --uid 1000 --gid appgroup --no-create-home appuser

COPY --from=build /app/publish .

RUN mkdir -p Logs && chown -R appuser:appgroup /app
USER appuser

#EXPOSE 6667/udp
#EXPOSE 6668/tcp

ENTRYPOINT ["dotnet", "MqttThSensorWorker.dll"]
