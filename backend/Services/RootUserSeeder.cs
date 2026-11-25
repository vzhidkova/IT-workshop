using LogHive.Models;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace LogHive.Services;

public static class RootUserSeeder
{
    public static async Task SeedAsync(
        IConfiguration configuration,
        NpgsqlDataSource dataSource)
    {
        // Получаем пароль из User Secrets
        var password = configuration["RootUser:Password"];

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Не задан пароль RootUser:Password");
        }

        // Создаём объект пользователя
        var user = new AppUser
        {
            Username = "root"
        };

        // Преобразуем пароль в безопасный хеш
        var passwordHasher = new PasswordHasher<AppUser>();

        user.PasswordHash = passwordHasher.HashPassword(
            user,
            password
        );

        // Открываем подключение к PostgreSQL
        await using var connection =
            await dataSource.OpenConnectionAsync();

        // Добавляем root, только если его ещё нет
        const string sql = """
            INSERT INTO users (username, password_hash)
            VALUES (@username, @passwordHash)
            ON CONFLICT (username) DO NOTHING;
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue("username", user.Username);
        command.Parameters.AddWithValue("passwordHash", user.PasswordHash);

        await command.ExecuteNonQueryAsync();
    }
}