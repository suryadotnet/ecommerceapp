FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/EcommAzureFunctions/EcommAzureFunctions.csproj src/EcommAzureFunctions/
RUN dotnet restore src/EcommAzureFunctions/EcommAzureFunctions.csproj
COPY src/EcommAzureFunctions/ src/EcommAzureFunctions/
RUN dotnet publish src/EcommAzureFunctions/EcommAzureFunctions.csproj \
    --configuration Release \
    --output /home/site/wwwroot \
    --no-restore

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS final
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true
WORKDIR /home/site/wwwroot
COPY --from=build /home/site/wwwroot .
EXPOSE 80
