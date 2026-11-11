FROM mcr.microsoft.com/dotnet/sdk:8.0.428 AS build
WORKDIR /src
COPY UserManagement.Api.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish UserManagement.Api.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0.22
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["dotnet", "UserManagement.Api.dll"]
