cd ../BaseService/BaseService.Host
start dotnet run

cd  ../../AuthServer/IdentityServer/AuthServer.Host
start dotnet run

cd ../../../MicroServices/Business/Business.Host
start dotnet run

cd ../../FileStorage/FileStorage.Host
start dotnet run

cd ../../../Gateways/WebAppGateway/WebAppGateway.Host
start dotnet run