namespace ETMS.Api.Models.Enums;

/// <summary>Payment state of a billable task (SRS FR-PAY-03). Paid is terminal.</summary>
public enum BillingStatus
{
    Unpaid = 0,
    Processing = 1,
    Paid = 2,
    Failed = 3
}
