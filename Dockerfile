FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /source

# copy csproj and restore as distinct layers
COPY ./TransXChange2GTFS_2/*.csproj .
RUN dotnet restore

# copy and publish app and libraries
COPY ./TransXChange2GTFS_2 .
RUN dotnet publish -c release -o /app --no-restore


# final stage/image
FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "TransXChange2GTFS_2.dll"]
