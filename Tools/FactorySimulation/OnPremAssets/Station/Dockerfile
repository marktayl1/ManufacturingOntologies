#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 4840

# ENV StationType=assembly
# ENV StationURI=opc.tcp://assembly.munich/
# ENV PowerConsumption=200
# ENV CycleTime=6
# ENV STORAGE_CONTAINER_NAME=ontologies
# ENV STORAGE_CONNECTION_STRING=DefaultEndpointsProtocol=https;AccountName=[yourstorageaccountname];AccountKey=[key];EndpointSuffix=core.windows.net

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Station.csproj", "."]
RUN dotnet restore "./Station.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "Station.csproj" -c debugging -o /app/build

FROM build AS publish
RUN dotnet publish "Station.csproj" -c debugging -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Station.dll"]