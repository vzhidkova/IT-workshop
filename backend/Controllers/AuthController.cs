
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using LogHive.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace LogHive.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IConfiguration _configuration;

    public AuthController(
        NpgsqlDataSource dataSource,
        IConfiguration configuration)
    {
        _dataSource = dataSource;
        _configuration = configuration;
    }

    // POST /api/auth/login
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync();

        const string sql = """
            SELECT id, username, password_hash
            FROM users
            WHERE username = @username;
            """;

        await using var command =
            new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(
            "username", request.Username);

        await using var reader =
            await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            return Unauthorized(new
            {
                message = "Неверный логин или пароль"
            });
        }

        var user = new AppUser
        {
            Id = reader.GetInt32(0),
            Username = reader.GetString(1),
            PasswordHash = reader.GetString(2)
        };

        await reader.CloseAsync();

        // Проверяем введённый пароль по сохранённому хешу
        var passwordHasher = new PasswordHasher<AppUser>();

        var result = passwordHasher.VerifyHashedPassword(
            user,
            user.PasswordHash,
            request.Password
        );

        if (result == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new
            {
                message = "Неверный логин или пароль"
            });
        }

        // Получаем настройки JWT
        var jwtSettings = _configuration.GetSection("Jwt");

        var secretKey = jwtSettings["SecretKey"]!;
        var issuer = jwtSettings["Issuer"]!;
        var audience = jwtSettings["Audience"]!;

        // Создаём данные пользователя внутри токена
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username)
        };

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(secretKey));

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256
        );

        // Создаём JWT сроком на 8 часов
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials
        );

        var tokenString =
            new JwtSecurityTokenHandler().WriteToken(token);

        return Ok(new
        {
            accessToken = tokenString,
            tokenType = "Bearer",
            expiresIn = 28800,
            user = new
            {
                user.Id,
                user.Username
            }
        });
    }

    // GET /api/auth/me
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        return Ok(new
        {
            id = User.FindFirstValue(
                ClaimTypes.NameIdentifier),

            username = User.Identity?.Name
        });
    }
}
