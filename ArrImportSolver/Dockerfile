FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ArrImportSolver/ArrImportSolver.csproj", "ArrImportSolver/"]
RUN dotnet restore "ArrImportSolver/ArrImportSolver.csproj"
COPY . .
WORKDIR "/src/ArrImportSolver"
RUN dotnet build "./ArrImportSolver.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./ArrImportSolver.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/data && chown -R $APP_UID /app/data
USER $APP_UID
ENTRYPOINT ["dotnet", "ArrImportSolver.dll"]
