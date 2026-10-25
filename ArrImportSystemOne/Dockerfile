FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ArrImportSystemOne/ArrImportSystemOne.csproj", "ArrImportSystemOne/"]
RUN dotnet restore "ArrImportSystemOne/ArrImportSystemOne.csproj"
COPY . .
WORKDIR "/src/ArrImportSystemOne"
RUN dotnet build "./ArrImportSystemOne.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ArrImportSystemOne.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER root
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
ENTRYPOINT ["dotnet", "ArrImportSystemOne.dll"]
