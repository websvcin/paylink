namespace Api.Models;

public class Webhook
{
    public int? Id { get; set; } // Auto Increment

    public int PaymentId { get; set; }

    public int GatewayId { get; set; }

    public string WebhookData { get; set; } // Raw JSON string

    public bool Processed { get; set; } = false;

    public DateTime? CreatedAt { get; set; }
}
