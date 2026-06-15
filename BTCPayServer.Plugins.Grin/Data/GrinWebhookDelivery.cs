using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Grin.Data;

/// <summary>
/// Persistent record of an outbound webhook delivery attempt.
/// One row per (invoice, eventType) enqueue — the worker
/// (<c>GrinWebhookDeliveryWorker</c>) walks rows in
/// <see cref="GrinWebhookDeliveryStatus.Pending"/> /
/// <see cref="GrinWebhookDeliveryStatus.Failed"/> state and re-attempts
/// delivery on an exponential-backoff schedule. After
/// <c>MaxAttempts</c> attempts a row goes to
/// <see cref="GrinWebhookDeliveryStatus.DeadLetter"/> and stops being
/// retried; an operator can manually reset <c>Status=Failed</c> and
/// <c>NextAttemptAt=now()</c> to nudge it again.
///
/// The payload is captured at enqueue-time (not regenerated per
/// attempt) so a settings change between attempts can't alter what
/// arrives at the merchant. The signature is recomputed per attempt
/// against the captured payload + the CURRENT secret, so rotating
/// the secret invalidates in-flight retries cleanly (the merchant
/// can no longer verify them).
/// </summary>
public class GrinWebhookDelivery
{
    [Key]
    public string Id { get; set; }              // ulid / guid

    public string InvoiceId { get; set; }       // FK to GrinInvoices.Id (not enforced — we keep the row after invoice purge for audit)
    public string StoreId { get; set; }         // BTCPay store ID — denormalized so we can resolve settings without joining
    public string EventType { get; set; }       // InvoicePaymentSettled / InvoiceExpired / InvoiceInvalid
    public string Url { get; set; }             // Snapshot of WebhookUrl at enqueue time
    public string Payload { get; set; }         // Snapshot JSON, signed against the live secret per attempt

    public GrinWebhookDeliveryStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastResponseCode { get; set; }
    public string LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public enum GrinWebhookDeliveryStatus
{
    /// <summary>Never attempted — ready for first delivery.</summary>
    Pending = 0,
    /// <summary>At least one attempt returned non-2xx or threw; waiting for <see cref="GrinWebhookDelivery.NextAttemptAt"/>.</summary>
    Failed = 1,
    /// <summary>Final HTTP response was 2xx. No further attempts.</summary>
    Delivered = 2,
    /// <summary>Exhausted retry schedule. Operator intervention required.</summary>
    DeadLetter = 3,
}
