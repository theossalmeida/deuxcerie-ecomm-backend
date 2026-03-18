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

    public async Task SendPaymentRefundedAsync(string toEmail, string clientName)
    {
        var subject = "Estorno processado — Deuxcerie";
        var html = $"""
            <p>Olá, <strong>{clientName}</strong>!</p>
            <p>O estorno do seu pagamento foi processado com sucesso.</p>
            <p>O valor será creditado de acordo com a política da sua instituição financeira.</p>
            <p>Se tiver dúvidas, entre em contato com nosso suporte.</p>
            <br/>
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
