
# Этап 1. Сборка приложения
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

COPY ["LogHive.csproj", "./"]

RUN dotnet restore "LogHive.csproj"

COPY . .

RUN dotnet publish "LogHive.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false


# Этап 2. Запуск приложения
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "LogHive.dll"]
