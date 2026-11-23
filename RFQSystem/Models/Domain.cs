namespace RFQSystem.Models;

/// <summary>詢價單狀態：企業詢比議價的標準流程</summary>
public enum RfqStatus
{
    Draft,          // 草稿
    Quoting,        // 詢價中（已發給供應商，等待報價）
    Comparing,      // 比價中（報價截止，開始比價）
    Negotiating,    // 議價中
    PendingApproval,// 決標簽核中
    Awarded,        // 已決標
    PoCreated,      // 已轉採購單
    Cancelled       // 已取消
}

public static class StatusText
{
    public static string Text(this RfqStatus s) => s switch
    {
        RfqStatus.Draft => "草稿",
        RfqStatus.Quoting => "詢價中",
        RfqStatus.Comparing => "比價中",
        RfqStatus.Negotiating => "議價中",
        RfqStatus.PendingApproval => "簽核中",
        RfqStatus.Awarded => "已決標",
        RfqStatus.PoCreated => "已轉採購單",
        RfqStatus.Cancelled => "已取消",
        _ => s.ToString()
    };

    public static string Badge(this RfqStatus s) => s switch
    {
        RfqStatus.Draft => "secondary",
        RfqStatus.Quoting => "primary",
        RfqStatus.Comparing => "info",
        RfqStatus.Negotiating => "warning",
        RfqStatus.PendingApproval => "danger",
        RfqStatus.Awarded => "success",
        RfqStatus.PoCreated => "dark",
        _ => "light text-dark border"
    };
}

public class Supplier
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Contact { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public List<string> Categories { get; set; } = new();
    /// <summary>品質評分 0-100（來自進料檢驗、客訴等）</summary>
    public int QualityScore { get; set; }
    /// <summary>準時交貨率 %</summary>
    public int OnTimeRate { get; set; }
    public string PaymentTerms { get; set; } = "月結60天";
    /// <summary>是否為合格供應商（AVL, Approved Vendor List）</summary>
    public bool IsQualified { get; set; } = true;
    public string Grade => (QualityScore * 0.6 + OnTimeRate * 0.4) switch
    {
        >= 90 => "A",
        >= 80 => "B",
        >= 70 => "C",
        _ => "D"
    };
}

public class Material
{
    public int Id { get; set; }
    public string PartNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Spec { get; set; } = "";
    public string Unit { get; set; } = "PCS";
    public string Category { get; set; } = "";
}

/// <summary>歷史成交價（用於價格趨勢與議價參考）</summary>
public class PriceHistory
{
    public int MaterialId { get; set; }
    public int SupplierId { get; set; }
    public DateTime Date { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Qty { get; set; }
    public string Source { get; set; } = "";
}

public class Rfq
{
    public int Id { get; set; }
    public string No { get; set; } = "";
    public string Title { get; set; } = "";
    public string Buyer { get; set; } = "";
    public string Department { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime Deadline { get; set; }
    public string Currency { get; set; } = "TWD";
    public string DeliveryPlace { get; set; } = "新竹廠";
    public string Remark { get; set; } = "";
    public RfqStatus Status { get; set; }
    public List<RfqLine> Lines { get; set; } = new();
    public List<int> InvitedSupplierIds { get; set; } = new();
    /// <summary>婉拒報價的供應商與原因</summary>
    public Dictionary<int, string> Declines { get; set; } = new();

    /// <summary>密封報價：開標前連採購也看不到報價內容</summary>
    public bool SealedBid { get; set; } = true;
    public DateTime? OpenedAt { get; set; }
    public string OpenedBy { get; set; } = "";
    /// <summary>監標人（通常由稽核或財務擔任，確保開標過程公正）</summary>
    public string Witness { get; set; } = "";
    /// <summary>開標紀錄：開標當下各廠商的報價快照</summary>
    public List<BidOpeningEntry> OpeningRecord { get; set; } = new();
    /// <summary>目前是否仍在密封狀態（價格不可見）</summary>
    public bool IsSealed => SealedBid && OpenedAt == null;
    public List<Quotation> Quotes { get; set; } = new();
    public List<NegotiationRequest> Negotiations { get; set; } = new();
    public List<Award> Awards { get; set; } = new();
    public string AwardReason { get; set; } = "";
    public List<ApprovalStep> Approvals { get; set; } = new();
    public List<AuditLog> Logs { get; set; } = new();

    public decimal TargetTotal => Lines.Sum(l => l.TargetPrice * l.Qty);
    public decimal AwardTotal => Awards.Sum(a => a.UnitPrice * a.Qty);
}

public class RfqLine
{
    public int Id { get; set; }
    public int MaterialId { get; set; }
    public decimal Qty { get; set; }
    /// <summary>目標價（採購內部預算，不揭露給供應商）</summary>
    public decimal TargetPrice { get; set; }
    public DateTime RequiredDate { get; set; }
}

/// <summary>供應商報價（同一家供應商可有多輪：第1輪為初次報價，之後為議價後重新報價）</summary>
public class Quotation
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public int Round { get; set; } = 1;
    public DateTime SubmittedAt { get; set; }
    public DateTime ValidUntil { get; set; }
    public string PaymentTerms { get; set; } = "";
    public string Remark { get; set; } = "";
    /// <summary>報價來源：入口網站（供應商自行填寫）/ 採購代登 / 模擬</summary>
    public string Source { get; set; } = "";
    public List<QuoteLine> Lines { get; set; } = new();
}

public class BidOpeningEntry
{
    public int SupplierId { get; set; }
    /// <summary>已投標 / 婉拒 / 未投標</summary>
    public string Result { get; set; } = "";
    public DateTime? SubmittedAt { get; set; }
    public decimal? Total { get; set; }
    public string Note { get; set; } = "";
}

public class QuoteLine
{
    public int RfqLineId { get; set; }
    /// <summary>以本次需求數量計算的單價（有階梯價時，自動取對應階梯）</summary>
    public decimal UnitPrice { get; set; }
    public int LeadTimeDays { get; set; }
    public decimal Moq { get; set; }
    /// <summary>階梯報價：數量越多單價越低（無階梯時為空，代表單一價格）</summary>
    public List<PriceTier> Tiers { get; set; } = new();

    /// <summary>計算指定數量的單價：取「起訂數量 ≤ 數量」的最高階梯；數量未達第一階時以第一階計價</summary>
    public decimal PriceAt(decimal qty) =>
        Tiers.Count == 0 ? UnitPrice
        : Tiers.OrderBy(t => t.MinQty).LastOrDefault(t => t.MinQty <= qty)?.UnitPrice ?? Tiers.MinBy(t => t.MinQty)!.UnitPrice;

    /// <summary>比目前數量更高的下一個階梯（用來提示「再多買一點更便宜」）</summary>
    public PriceTier? NextTier(decimal qty) => Tiers.Where(t => t.MinQty > qty).MinBy(t => t.MinQty);

    public string TierText => string.Join(" / ", Tiers.OrderBy(t => t.MinQty).Select(t => $"≥{t.MinQty:N0}：{t.UnitPrice:N2}"));
}

public class PriceTier
{
    /// <summary>起訂數量（數量 ≥ 此值適用此單價）</summary>
    public decimal MinQty { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>議價請求（採購對供應商提出還價）</summary>
public class NegotiationRequest
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    /// <summary>此議價會產生的報價輪次</summary>
    public int Round { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Message { get; set; } = "";
    public Dictionary<int, decimal> CounterPrices { get; set; } = new();
    public bool Replied { get; set; }
    public string Reply { get; set; } = "";
}

/// <summary>決標結果：可按料號分開決標（分批採購 / Split Award）</summary>
public class Award
{
    public int RfqLineId { get; set; }
    public int SupplierId { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
}

public class ApprovalStep
{
    public int Seq { get; set; }
    public string Role { get; set; } = "";
    public string Approver { get; set; } = "";
    /// <summary>Pending / Approved / Rejected / Waiting</summary>
    public string Status { get; set; } = "Waiting";
    public string Comment { get; set; } = "";
    public DateTime? At { get; set; }
}

public class AuditLog
{
    public DateTime At { get; set; }
    public string User { get; set; } = "";
    public string Action { get; set; } = "";
}

public class PurchaseOrder
{
    public int Id { get; set; }
    public string No { get; set; } = "";
    /// <summary>來源詢價單（依合約下單時為 0）</summary>
    public int RfqId { get; set; }
    /// <summary>來源合約（由詢價決標產生時為 null）</summary>
    public int? ContractId { get; set; }
    public string DeliveryPlace { get; set; } = "新竹廠";
    public int SupplierId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string PaymentTerms { get; set; } = "";
    public string Status { get; set; } = "已發出";
    public DateTime? ConfirmedAt { get; set; }
    public List<PoLine> Lines { get; set; } = new();
    public decimal Total => Lines.Sum(l => l.Qty * l.UnitPrice);
}

/// <summary>
/// 合約價（Price Agreement）：決標後與供應商約定一段期間內的固定價格。
/// 有效期間內需要這個料件時，直接依合約下單，不必每次重新詢價。
/// </summary>
public class PriceContract
{
    public int Id { get; set; }
    public string No { get; set; } = "";
    public int SupplierId { get; set; }
    public int MaterialId { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>合約階梯價（沿用決標時的階梯報價）</summary>
    public List<PriceTier> Tiers { get; set; } = new();
    public string Currency { get; set; } = "TWD";
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    /// <summary>合約期間承諾採購量（0＝無承諾量）</summary>
    public decimal CommittedQty { get; set; }
    public decimal Moq { get; set; } = 1;
    public int LeadTimeDays { get; set; }
    public string PaymentTerms { get; set; } = "";
    /// <summary>來源詢價單（舊合約或手動建立時為 null）</summary>
    public int? SourceRfqId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Remark { get; set; } = "";
    public bool Terminated { get; set; }
    public string TerminateReason { get; set; } = "";
    /// <summary>由哪張合約續約而來 / 被哪張合約取代</summary>
    public int? RenewedFromId { get; set; }
    public List<AuditLog> Logs { get; set; } = new();

    public const int ExpiringDays = 30;

    public string Status => Terminated ? "已終止"
        : DateTime.Today < ValidFrom.Date ? "未生效"
        : DateTime.Today > ValidTo.Date ? "已到期"
        : (ValidTo.Date - DateTime.Today).TotalDays <= ExpiringDays ? "即將到期"
        : "生效中";

    public bool IsActive => Status is "生效中" or "即將到期";

    public string StatusBadge => Status switch
    {
        "生效中" => "success",
        "即將到期" => "warning text-dark",
        "未生效" => "info",
        "已終止" => "danger",
        _ => "secondary"
    };

    public int DaysLeft => (int)(ValidTo.Date - DateTime.Today).TotalDays;

    /// <summary>依數量計算合約單價（有階梯時取對應階梯）</summary>
    public decimal PriceAt(decimal qty) =>
        Tiers.Count == 0 ? UnitPrice
        : Tiers.OrderBy(t => t.MinQty).LastOrDefault(t => t.MinQty <= qty)?.UnitPrice ?? Tiers.MinBy(t => t.MinQty)!.UnitPrice;

    public string TierText => string.Join(" / ", Tiers.OrderBy(t => t.MinQty).Select(t => $"≥{t.MinQty:N0}：{t.UnitPrice:N2}"));
}

public class PoLine
{
    public int MaterialId { get; set; }
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime DeliveryDate { get; set; }
}
