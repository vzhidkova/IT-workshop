
# Этап 1. Сборка приложения
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

# Копируем файл проекта
COPY ["LogHive.csproj", "./"]

# Восстанавливаем NuGet-зависимости
RUN dotnet restore "LogHive.csproj"

# Копируем исходный код
COPY . .

# Собираем приложение
RUN dotnet publish "LogHive.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false


# Этап 2. Запуск приложения
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "LogHive.dll"]
