using System.Net.Http.Json;

namespace EcommerceApi.Infrastructure.Services;

public class EmailService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<EmailService> logger)
{
    private string From => config["Resend:FromEmail"] ?? "Deuxcerie <noreply@deuxcerie.com.br>";

    public async Task SendOrderConfirmedAsync(string toEmail, string clientName, Guid orderId)
    {
        var subject = "Pedido confirmado — Deuxcerie";
        var html = $"""
            <p>Olá, <strong>{clientName}</strong>!</p>
            <p>Seu pagamento foi confirmado e seu pedido está sendo preparado. 🎉</p>
            <p><strong>Número do pedido:</strong> {orderId}</p>
            <p>Em breve entraremos em contato com mais detalhes.</p>
            <br/>
            <p>Atenciosamente,<br/>Equipe Deuxcerie</p>
            """;

        await SendAsync(toEmail, subject, html);
    }

    public async Task SendOrderCancelledAsync(string toEmail, string clientName, Guid? orderId)
    {
        var subject = "Pedido cancelado — Deuxcerie";
        var orderInfo = orderId.HasValue ? $"<p><strong>Pedido:</strong> #{orderId.Value}</p>" : "";
        var html = $"""
            <p>Olá, <strong>{clientName}</strong>,</p>
            <p>Lamentamos informar que o seu pedido foi cancelado.</p>
            {orderInfo}
            <p>O reembolso do valor pago já foi solicitado e será creditado automaticamente. O prazo pode variar de acordo com a sua instituição financeira:</p>
            <ul>
              <li><strong>PIX:</strong> em até 1 dia útil</li>
              <li><strong>Cartão de crédito:</strong> em até 2 faturas</li>
            </ul>
            <p>Se você não solicitou este cancelamento ou tiver qualquer dúvida, entre em contato com nosso suporte — faremos o possível para ajudar.</p>
            <br/>
            <p>Pedimos desculpas pelo inconveniente.</p>
            <p>Atenciosamente,<br/>Equipe Deuxcerie</p>
            """;

        await SendAsync(toEmail, subject, html);
    }

    public async Task SendPaymentDisputedAsync(string toEmail, string clientName)
    {
        var subject = "Disputa de pagamento identificada — Deuxcerie";
        var html = $"""
            <p>Olá, <strong>{clientName}</strong>!</p>
            <p>Identificamos uma disputa (chargeback) relacionada ao seu pagamento.</p>
            <p>Nosso time irá analisar o caso e entrar em contato em breve.</p>
            <p>Se isso foi um engano, por favor entre em contato com nosso suporte imediatamente.</p>
            <br/>
            <p>Atenciosamente,<br/>Equipe Deuxcerie</p>
            """;

        await SendAsync(toEmail, subject, html);
    }

    private async Task SendAsync(string toEmail, string subject, string html)
    {
        var client = httpClientFactory.CreateClient("Resend");
        try
        {
            var payload = new
            {
                from = From,
                to = new[] { toEmail },
                reply_to = new[] { "pedidos@deuxcerie.com.br" },
                subject,
                html
            };

            var response = await client.PostAsJsonAsync("/emails", payload);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                logger.LogError("Resend falhou ao enviar email para {To} — {Status}: {Body}", toEmail, response.StatusCode, content);
            else
                logger.LogInformation("Email enviado para {To}: {Subject}", toEmail, subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resend exception ao enviar email para {To}", toEmail);
        }
    }
}
