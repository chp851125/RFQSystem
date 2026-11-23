using RFQSystem.Services;

namespace RFQSystem.Models;

public class RfqDetailVm
{
    public Rfq Rfq { get; set; } = null!;
    public MockDb Db { get; set; } = null!;
    public string Tab { get; set; } = "info";
    public CompareResult Compare { get; set; } = null!;
    public List<PurchaseOrder> Pos { get; set; } = new();
}

public class SupplierCompare
{
    public Supplier Supplier { get; set; } = null!;
    public Quotation? Latest { get; set; }
    public Quotation? First { get; set; }
    public decimal Total { get; set; }
    public decimal FirstTotal { get; set; }
    public bool QuotedAll { get; set; }
    public double AvgLead { get; set; }
    public double PriceScore { get; set; }
    public double LeadScore { get; set; }
    public double QualityScore { get; set; }
    public double Score { get; set; }
    public int Rank { get; set; }
}

public class LineCompare
{
    public RfqLine Line { get; set; } = null!;
    public Material Material { get; set; } = null!;
    public Dictionary<int, QuoteLine> Cells { get; set; } = new();
    public decimal? MinPrice { get; set; }
    public int? MinSupplierId { get; set; }
    public decimal? MaxPrice { get; set; }
    public decimal? LastPurchasePrice { get; set; }
}

/// <summary>比價分析：價格 / 交期 / 品質 加權評分</summary>
public class CompareResult
{
    public int PriceWeight { get; set; } = 60;
    public int LeadWeight { get; set; } = 20;
    public int QualityWeight { get; set; } = 20;
    public List<SupplierCompare> Suppliers { get; set; } = new();
    public List<LineCompare> Lines { get; set; } = new();
    /// <summary>每項都挑最低價的總額（Cherry-picking）</summary>
    public decimal LowestPerLineTotal { get; set; }
    public SupplierCompare? BestScore => Suppliers.Where(s => s.QuotedAll).OrderBy(s => s.Rank).FirstOrDefault();

    public static CompareResult Build(Rfq rfq, MockDb db, int pw = 60, int lw = 20, int qw = 20)
    {
        var r = new CompareResult { PriceWeight = pw, LeadWeight = lw, QualityWeight = qw };
        foreach (var line in rfq.Lines)
        {
            var lc = new LineCompare
            {
                Line = line,
                Material = db.Material(line.MaterialId),
                LastPurchasePrice = db.PriceHistories.Where(h => h.MaterialId == line.MaterialId)
                    .OrderByDescending(h => h.Date).FirstOrDefault()?.UnitPrice
            };
            foreach (var sid in rfq.InvitedSupplierIds)
            {
                var ql = MockDb.LatestQuote(rfq, sid)?.Lines.FirstOrDefault(l => l.RfqLineId == line.Id);
                if (ql != null) lc.Cells[sid] = ql;
            }
            if (lc.Cells.Count > 0)
            {
                var min = lc.Cells.MinBy(c => c.Value.UnitPrice);
                lc.MinPrice = min.Value.UnitPrice;
                lc.MinSupplierId = min.Key;
                lc.MaxPrice = lc.Cells.Max(c => c.Value.UnitPrice);
            }
            r.Lines.Add(lc);
        }
        r.LowestPerLineTotal = r.Lines.Where(l => l.MinPrice.HasValue).Sum(l => l.MinPrice!.Value * l.Line.Qty);

        foreach (var sid in rfq.InvitedSupplierIds)
        {
            var latest = MockDb.LatestQuote(rfq, sid);
            if (latest == null) { r.Suppliers.Add(new SupplierCompare { Supplier = db.Supplier(sid) }); continue; }
            var first = MockDb.FirstQuote(rfq, sid)!;
            decimal Sum(Quotation q) => q.Lines.Sum(l => l.UnitPrice * rfq.Lines.First(x => x.Id == l.RfqLineId).Qty);
            var s = db.Supplier(sid);
            r.Suppliers.Add(new SupplierCompare
            {
                Supplier = s,
                Latest = latest,
                First = first,
                Total = Sum(latest),
                FirstTotal = Sum(first),
                QuotedAll = latest.Lines.Count == rfq.Lines.Count,
                AvgLead = latest.Lines.Average(l => l.LeadTimeDays),
                QualityScore = s.QualityScore * 0.6 + s.OnTimeRate * 0.4
            });
        }

        var quoted = r.Suppliers.Where(s => s.Latest != null).ToList();
        if (quoted.Count > 0)
        {
            var minTotal = quoted.Min(s => s.Total);
            var minLead = quoted.Min(s => s.AvgLead);
            var totalWeight = Math.Max(1, pw + lw + qw);
            foreach (var s in quoted)
            {
                s.PriceScore = (double)(minTotal / s.Total) * 100;
                s.LeadScore = minLead / s.AvgLead * 100;
                s.Score = (s.PriceScore * pw + s.LeadScore * lw + s.QualityScore * qw) / totalWeight;
            }
            var rank = 1;
            foreach (var s in quoted.OrderByDescending(s => s.Score)) s.Rank = rank++;
        }
        return r;
    }
}

/// <summary>階梯報價編輯器（報價表單中每個項目下方的一列）</summary>
public record TierEditorVm(int LineId, decimal Qty, string Unit, List<PriceTier> Tiers, int Colspan);

/// <summary>把報價表單的欄位組成報價項目（採購代登與供應商入口共用）</summary>
public static class QuoteForm
{
    public const int TierSlots = 4;

    public static List<QuoteLine> BuildLines(Dictionary<int, decimal> prices, Dictionary<int, int> leads, Dictionary<int, decimal> moqs,
        List<int>? tierLine, List<decimal?>? tierQty, List<decimal?>? tierPrice)
    {
        tierLine ??= new(); tierQty ??= new(); tierPrice ??= new();
        return prices.Select(p => new QuoteLine
        {
            RfqLineId = p.Key,
            UnitPrice = p.Value,
            LeadTimeDays = leads.GetValueOrDefault(p.Key),
            Moq = moqs.GetValueOrDefault(p.Key),
            // 三個欄位平行排列：第 i 格屬於 tierLine[i] 這個項目；數量、單價都空白的格子略過
            Tiers = Enumerable.Range(0, tierLine.Count)
                .Where(i => tierLine[i] == p.Key && i < tierQty.Count && i < tierPrice.Count && (tierQty[i].HasValue || tierPrice[i].HasValue))
                .Select(i => new PriceTier { MinQty = tierQty[i] ?? 0, UnitPrice = tierPrice[i] ?? 0 })
                .ToList()
        }).ToList();
    }
}

public class RfqCreateVm
{
    public string? Title { get; set; }
    public string? Department { get; set; }
    public DateTime Deadline { get; set; } = DateTime.Today.AddDays(7);
    public string? DeliveryPlace { get; set; } = "新竹廠";
    public string? Remark { get; set; }
    public bool SealedBid { get; set; } = true;
    public List<RfqLineInput> Lines { get; set; } = new();
}

public class RfqLineInput
{
    public int MaterialId { get; set; }
    public decimal Qty { get; set; }
    public decimal TargetPrice { get; set; }
    public DateTime RequiredDate { get; set; } = DateTime.Today.AddDays(30);
}
