namespace FacilApp.Sql.Api.Models;

public sealed class ZApiWebhookRequest
{
    public string InstanceId { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string MessageId { get; set; } = string.Empty;

    public bool FromMe { get; set; }

    public bool FromApi { get; set; }

    public bool IsGroup { get; set; }

    public bool IsStatusReply { get; set; }

    public bool IsEdit { get; set; }

    public bool IsNewsletter { get; set; }

    public ZApiText Text { get; set; } = new ZApiText();
}
