using Kairo.Core.Models;

namespace Kairo.Host;

/// <summary>Turns provider failures into short, actionable messages for the UI.</summary>
public static class ErrorText
{
    public static string Describe(Exception ex, string providerId)
    {
        var status = StatusOf(ex);
        var urlHint = providerId == ProviderFactory.OpenAICompatible
            ? " Для OpenAI-совместимых сервисов адрес обычно заканчивается на /v1."
            : "";

        return status switch
        {
            401 or 403 => $"Сервер отклонил API-ключ ({status}). Проверьте ключ в настройках.",
            404 or 405 => $"По этому адресу нет API ({status}). Проверьте адрес в настройках.{urlHint}",
            429 => "Превышен лимит запросов (429). Подождите немного и повторите.",
            400 or 422 => $"Сервер отклонил запрос ({status}): {Detail(ex)}",
            >= 500 => $"Сервер API ответил ошибкой {status}. Если она повторяется, проверьте адрес API в настройках.{urlHint}",
            _ when ex is HttpRequestException => $"Не удалось подключиться к серверу API: {ex.Message}",
            _ => ex.Message,
        };
    }

    /// <summary>HTTP status from our own provider errors or the Anthropic SDK's typed exceptions.</summary>
    internal static int? StatusOf(Exception ex) => ex switch
    {
        ModelProviderException { StatusCode: { } code } => code,
        HttpRequestException { StatusCode: { } code } => (int)code,
        _ => ex.GetType().Name switch
        {
            "AnthropicBadRequestException" => 400,
            "AnthropicUnauthorizedException" => 401,
            "AnthropicForbiddenException" => 403,
            "AnthropicNotFoundException" => 404,
            "AnthropicUnprocessableEntityException" => 422,
            "AnthropicRateLimitException" => 429,
            "Anthropic5xxException" => ParseStatus(ex.Message) ?? 500,
            _ => null,
        },
    };

    private static int? ParseStatus(string message)
    {
        var m = System.Text.RegularExpressions.Regex.Match(message, @"\b(5\d\d)\b");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string Detail(Exception ex)
    {
        var text = ex.Message.ReplaceLineEndings(" ");
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
