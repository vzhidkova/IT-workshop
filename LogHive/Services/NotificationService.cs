
using System.Net.Http.Json;

namespace LogHive.Services;

public class NotificationService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public NotificationService(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task SendNotificationAsync(
        string message,
        CancellationToken cancellationToken)
    {
        var webhookUrl =
            _configuration["Mattermost:WebhookUrl"];

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            throw new InvalidOperationException(
                "Mattermost WebhookUrl is not configured");
        }

        var payload = new
        {
            text = message
        };

        using var response = await _httpClient.PostAsJsonAsync(
            webhookUrl,
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
