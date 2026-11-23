using RFQSystem.Models;

namespace RFQSystem.Services;

/// <summary>
/// 記憶體內的假資料庫（Singleton）。重新啟動網站後資料會回到初始狀態。
/// 所有業務規則（詢價→比價→議價→決標→簽核→轉採購單）都集中在這裡，方便閱讀。
/// </summary>
public class MockDb
{
    private readonly object _lock = new();
    private Random _rand = new(42);
    private DateTime? _clock; // 產生假資料時使用的虛擬時間

    public const string CurrentUser = "王小華（採購專員）";

    public List<Supplier> Suppliers { get; } = new();
    public List<Material> Materials { get; } = new();
    public List<Rfq> Rfqs { get; } = new();
    public List<PurchaseOrder> PurchaseOrders { get; } = new();
    public List<PriceHistory> PriceHistories { get; } = new();
    public List<PriceContract> Contracts { get; } = new();

    /// <summary>簽核規則：依決標金額決定要簽到哪一層（企業常見的「授權金額」規則）</summary>
    public static readonly (decimal Threshold, string Role, string Approver)[] ApprovalMatrix =
    {
        (0m,         "採購主管", "李志明"),
        (500_000m,   "財務協理", "陳美玲"),
        (2_000_000m, "總經理",   "張國華"),
    };

    public const int MinSuppliersForCompare = 3;

    private DateTime Now => _clock ?? DateTime.Now;

    public MockDb()
    {
        Seed();
    }

    /// <summary>重置示範資料：清空所有異動，回到初始範例</summary>
    public void Reset()
    {
        lock (_lock)
        {
            Suppliers.Clear(); Materials.Clear(); Rfqs.Clear(); PurchaseOrders.Clear(); PriceHistories.Clear(); Contracts.Clear();
            _rand = new Random(42);
            Seed();
        }
    }

    public Supplier Supplier(int id) => Suppliers.First(s => s.Id == id);
    public Material Material(int id) => Materials.First(m => m.Id == id);
    public Rfq? FindRfq(int id) => Rfqs.FirstOrDefault(r => r.Id == id);

    /// <summary>取得某供應商最新一輪報價</summary>
    public static Quotation? LatestQuote(Rfq rfq, int supplierId) =>
        rfq.Quotes.Where(q => q.SupplierId == supplierId).OrderByDescending(q => q.Round).FirstOrDefault();

    public static Quotation? FirstQuote(Rfq rfq, int supplierId) =>
        rfq.Quotes.Where(q => q.SupplierId == supplierId).OrderBy(q => q.Round).FirstOrDefault();

    /// <summary>議價節省金額 = (第一輪報價 - 最終決標價) × 數量</summary>
    public static decimal NegotiationSavings(Rfq rfq) => rfq.Awards.Sum(a =>
    {
        var first = FirstQuote(rfq, a.SupplierId)?.Lines.FirstOrDefault(l => l.RfqLineId == a.RfqLineId);
        return first == null ? 0 : (first.UnitPrice - a.UnitPrice) * a.Qty;
    });

    private void Log(Rfq rfq, string action, string? user = null) =>
        rfq.Logs.Add(new AuditLog { At = Now, User = user ?? CurrentUser, Action = action });

    // ------------------------------------------------------------------
    // 業務操作
    // ------------------------------------------------------------------

    public Rfq CreateRfq(Rfq rfq)
    {
        lock (_lock)
        {
            rfq.Id = Rfqs.Count == 0 ? 1 : Rfqs.Max(r => r.Id) + 1;
            rfq.No = $"RFQ-{Now:yyyy}-{rfq.Id:0000}";
            rfq.CreatedAt = Now;
            rfq.Status = RfqStatus.Draft;
            if (string.IsNullOrWhiteSpace(rfq.Buyer)) rfq.Buyer = "王小華";
            var lineId = 1;
            foreach (var l in rfq.Lines) l.Id = lineId++;
            Rfqs.Add(rfq);
            Log(rfq, "建立詢價單（草稿）");
            return rfq;
        }
    }

    public void InviteSuppliers(int rfqId, List<int> supplierIds)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status == RfqStatus.Draft, "只有草稿狀態可以調整邀請的供應商");
            rfq.InvitedSupplierIds = supplierIds.Distinct().ToList();
            Log(rfq, "設定邀請供應商：" + string.Join("、", rfq.InvitedSupplierIds.Select(id => Supplier(id).Name)));
        }
    }

    /// <summary>發出詢價（實務上會 Email 通知供應商或開放供應商入口網站報價）</summary>
    public string? Send(int rfqId)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status == RfqStatus.Draft, "只有草稿可以發出");
            Require(rfq.Lines.Count > 0, "請至少輸入一個詢價項目");
            Require(rfq.InvitedSupplierIds.Count > 0, "請至少邀請一家供應商");
            Require(rfq.InvitedSupplierIds.All(id => Supplier(id).IsQualified), "邀請名單中有非合格供應商（未列入 AVL），請先移除");
            rfq.Status = RfqStatus.Quoting;
            Log(rfq, $"發出詢價給 {rfq.InvitedSupplierIds.Count} 家供應商，報價截止 {rfq.Deadline:yyyy/MM/dd}");
            return rfq.InvitedSupplierIds.Count < MinSuppliersForCompare
                ? $"提醒：邀請家數少於 {MinSuppliersForCompare} 家，多數企業內控要求「三家比價」，若不足需填寫理由（如獨家供應）。"
                : null;
        }
    }

    /// <summary>登錄供應商報價（第 1 輪＝初次報價；第 2 輪以後＝議價後重新報價）</summary>
    /// <summary>報價截止時間：截止日當天 23:59 前</summary>
    public bool IsPastDeadline(Rfq rfq) => Now >= rfq.Deadline.Date.AddDays(1);

    /// <param name="fromPortal">true＝供應商從入口網站自行報價（需遵守截止時間）；false＝採購代為登錄</param>
    public void SubmitQuote(int rfqId, Quotation quote, bool fromPortal = false)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.InvitedSupplierIds.Contains(quote.SupplierId), "此供應商不在邀請名單中");
            foreach (var ql in quote.Lines) NormalizeTiers(rfq, ql);
            Require(quote.Lines.Count == rfq.Lines.Count && quote.Lines.All(l => l.UnitPrice > 0), "每個項目都需要填寫大於 0 的單價");
            if (string.IsNullOrEmpty(quote.Source)) quote.Source = fromPortal ? "入口網站" : "採購代登";
            var name = Supplier(quote.SupplierId).Name;
            var latest = LatestQuote(rfq, quote.SupplierId);
            var pending = rfq.Negotiations.FirstOrDefault(n => n.SupplierId == quote.SupplierId && !n.Replied);
            if (pending == null)
            {
                Require(rfq.Status == RfqStatus.Quoting, "目前不在報價期間");
                Require(!fromPortal || !IsPastDeadline(rfq), "已超過報價截止時間，無法報價");
                rfq.Declines.Remove(quote.SupplierId);
                if (latest != null)
                {
                    // 截止前可修改報價：覆蓋原本的第 1 輪報價
                    latest.Lines = quote.Lines;
                    latest.SubmittedAt = Now;
                    latest.PaymentTerms = string.IsNullOrEmpty(quote.PaymentTerms) ? latest.PaymentTerms : quote.PaymentTerms;
                    if (quote.ValidUntil != default) latest.ValidUntil = quote.ValidUntil;
                    latest.Remark = quote.Remark;
                    latest.Source = quote.Source;
                    Log(rfq, $"{name} 於截止前修改報價（{quote.Source}）{AmountText(rfq, latest)}", name);
                    return;
                }
                quote.Round = 1;
            }
            else
            {
                quote.Round = latest!.Round + 1;
                pending.Replied = true;
                pending.Reply = string.IsNullOrEmpty(quote.Remark) ? "已重新報價" : quote.Remark;
            }
            quote.Id = rfq.Quotes.Count + 1;
            quote.SubmittedAt = Now;
            if (quote.ValidUntil == default) quote.ValidUntil = Now.Date.AddDays(30);
            if (string.IsNullOrEmpty(quote.PaymentTerms)) quote.PaymentTerms = Supplier(quote.SupplierId).PaymentTerms;
            rfq.Quotes.Add(quote);
            Log(rfq, $"{name} 提交第 {quote.Round} 輪報價（{quote.Source}）{AmountText(rfq, quote)}", name);
        }
    }

    private static decimal NiceQty(decimal q) => q >= 100 ? Math.Round(q / 100) * 100 : Math.Max(1, Math.Round(q));

    /// <summary>議價後依新單價等比例調整整組階梯價，並讓需求數量那一階剛好等於新單價</summary>
    public static List<PriceTier> ScaleTiers(QuoteLine old, decimal newPrice, decimal qty)
    {
        if (old.Tiers.Count == 0 || old.UnitPrice == 0) return new();
        var f = newPrice / old.UnitPrice;
        var tiers = old.Tiers.OrderBy(t => t.MinQty).Select(t => new PriceTier { MinQty = t.MinQty, UnitPrice = Math.Round(t.UnitPrice * f, 2) }).ToList();
        var h = Math.Max(0, tiers.FindLastIndex(t => t.MinQty <= qty));
        tiers[h].UnitPrice = newPrice;
        // 四捨五入後若破壞「量大價低」，以需求數量那一階為基準修正前後階梯
        for (var i = h + 1; i < tiers.Count; i++) tiers[i].UnitPrice = Math.Min(tiers[i].UnitPrice, tiers[i - 1].UnitPrice);
        for (var i = h - 1; i >= 0; i--) tiers[i].UnitPrice = Math.Max(tiers[i].UnitPrice, tiers[i + 1].UnitPrice);
        return tiers;
    }

    /// <summary>
    /// 檢查並整理階梯報價：起訂數量需遞增且不重複、數量越大單價不可越高；
    /// 有階梯時，單價自動改為「本次需求數量」對應的階梯價。
    /// </summary>
    private void NormalizeTiers(Rfq rfq, QuoteLine ql)
    {
        if (ql.Tiers.Count == 0) return;
        var line = rfq.Lines.FirstOrDefault(l => l.Id == ql.RfqLineId);
        Require(line != null, "報價項目不存在");
        var mat = Material(line!.MaterialId).PartNo;
        Require(ql.Tiers.All(t => t.MinQty > 0 && t.UnitPrice > 0), $"{mat} 階梯報價的數量與單價都必須大於 0");
        ql.Tiers = ql.Tiers.OrderBy(t => t.MinQty).ToList();
        Require(ql.Tiers.Select(t => t.MinQty).Distinct().Count() == ql.Tiers.Count, $"{mat} 階梯報價的起訂數量不可重複");
        for (var i = 1; i < ql.Tiers.Count; i++)
            Require(ql.Tiers[i].UnitPrice <= ql.Tiers[i - 1].UnitPrice, $"{mat} 階梯報價：數量越多，單價應相同或更低");
        ql.UnitPrice = ql.PriceAt(line.Qty);
        if (ql.Moq <= 0 || ql.Moq < ql.Tiers[0].MinQty) ql.Moq = ql.Tiers[0].MinQty;
    }

    /// <summary>密封期間的紀錄不寫金額，避免從異動紀錄洩漏報價</summary>
    private static string AmountText(Rfq rfq, Quotation q) =>
        rfq.IsSealed ? "，內容密封至開標" : $"，總額 {QuoteTotal(rfq, q):N0}";

    public static decimal QuoteTotal(Rfq rfq, Quotation q) =>
        q.Lines.Sum(l => l.UnitPrice * rfq.Lines.First(x => x.Id == l.RfqLineId).Qty);

    /// <summary>供應商婉拒報價（例如產能不足、非專長品項）</summary>
    public void DeclineQuote(int rfqId, int supplierId, string reason)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.InvitedSupplierIds.Contains(supplierId), "此供應商不在邀請名單中");
            Require(rfq.Status == RfqStatus.Quoting && !IsPastDeadline(rfq), "目前不在報價期間");
            Require(LatestQuote(rfq, supplierId) == null, "已報價，無法婉拒");
            rfq.Declines[supplierId] = string.IsNullOrWhiteSpace(reason) ? "未說明" : reason;
            var name = Supplier(supplierId).Name;
            Log(rfq, $"{name} 婉拒報價：{rfq.Declines[supplierId]}", name);
        }
    }

    /// <summary>模擬尚未報價的供應商自動報價（教學用）</summary>
    public int SimulateQuotes(int rfqId)
    {
        var rfq = Get(rfqId);
        var count = 0;
        foreach (var sid in rfq.InvitedSupplierIds.Where(id => LatestQuote(rfq, id) == null && !rfq.Declines.ContainsKey(id)).ToList())
        {
            var s = Supplier(sid);
            // 品質好的供應商通常較貴一點
            var bias = 0.95 + (s.QualityScore - 70) / 200.0;
            SubmitQuote(rfqId, new Quotation
            {
                SupplierId = sid,
                Lines = rfq.Lines.Select(l =>
                {
                    var price = Math.Round(l.TargetPrice * (decimal)(bias + _rand.NextDouble() * 0.2), 1);
                    var ql = new QuoteLine
                    {
                        RfqLineId = l.Id,
                        UnitPrice = price,
                        LeadTimeDays = _rand.Next(2, 9) * 7,
                        Moq = Math.Max(1, Math.Round(l.Qty * (decimal)(_rand.Next(1, 5) / 10.0)))
                    };
                    // 約一半的供應商會提供階梯報價：少量較貴、大量較便宜
                    if (_rand.NextDouble() < 0.5 && l.Qty >= 10)
                        ql.Tiers = new()
                        {
                            new() { MinQty = NiceQty(l.Qty * 0.5m), UnitPrice = Math.Round(price * 1.06m, 1) },
                            new() { MinQty = l.Qty, UnitPrice = price },
                            new() { MinQty = NiceQty(l.Qty * 2), UnitPrice = Math.Round(price * 0.95m, 1) },
                        };
                    return ql;
                }).ToList(),
                Remark = "模擬報價",
                Source = "模擬"
            });
            count++;
        }
        return count;
    }

    /// <summary>可擔任監標人的人員（開標時需有第二人在場見證）</summary>
    public static readonly string[] Witnesses = { "周建國（稽核室）", "陳美玲（財務協理）", "李志明（採購主管）" };

    /// <summary>所有受邀供應商都已投標或婉拒</summary>
    public static bool AllResponded(Rfq rfq) =>
        rfq.InvitedSupplierIds.All(id => LatestQuote(rfq, id) != null || rfq.Declines.ContainsKey(id));

    /// <summary>能否開標：需過截止時間，或所有受邀廠商都已回覆（提前開標）</summary>
    public (bool Ok, string Reason) CanOpen(Rfq rfq)
    {
        if (!rfq.IsSealed || rfq.Status != RfqStatus.Quoting) return (false, "此詢價單不在待開標狀態");
        if (rfq.Quotes.Count == 0) return (false, "尚無任何廠商投標");
        if (IsPastDeadline(rfq)) return (true, "已過報價截止時間");
        if (AllResponded(rfq)) return (true, "所有受邀廠商皆已回覆，可提前開標");
        return (false, $"尚未到截止時間（{rfq.Deadline:yyyy/MM/dd} 23:59），且仍有廠商未回覆");
    }

    /// <summary>開標：解除密封、記錄開標人與監標人、留下開標紀錄，並進入比價</summary>
    public void OpenBids(int rfqId, string witness)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            var (ok, reason) = CanOpen(rfq);
            Require(ok, reason);
            Require(!string.IsNullOrWhiteSpace(witness), "開標需指定監標人");
            Require(!witness.StartsWith(rfq.Buyer), "監標人不可為承辦採購本人");
            rfq.OpenedAt = Now;
            rfq.OpenedBy = CurrentUser;
            rfq.Witness = witness;
            rfq.OpeningRecord = rfq.InvitedSupplierIds.Select(id =>
            {
                var q = LatestQuote(rfq, id);
                if (q != null) return new BidOpeningEntry { SupplierId = id, Result = "已投標", SubmittedAt = q.SubmittedAt, Total = QuoteTotal(rfq, q), Note = q.Remark };
                if (rfq.Declines.TryGetValue(id, out var why)) return new BidOpeningEntry { SupplierId = id, Result = "婉拒", Note = why };
                return new BidOpeningEntry { SupplierId = id, Result = "未投標" };
            }).ToList();
            rfq.Status = RfqStatus.Comparing;
            var bids = rfq.OpeningRecord.Count(e => e.Result == "已投標");
            Log(rfq, $"開標（{reason}），監標人：{witness}，有效投標 {bids}/{rfq.InvitedSupplierIds.Count} 家");
        }
    }

    /// <summary>截止報價，進入比價（公開報價用；密封報價請用開標）</summary>
    public void CloseQuoting(int rfqId)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status == RfqStatus.Quoting, "目前不在報價期間");
            Require(!rfq.SealedBid, "密封報價需經「開標」程序才能進入比價");
            Require(rfq.Quotes.Count > 0, "尚無任何供應商報價，無法進入比價");
            rfq.Status = RfqStatus.Comparing;
            var quoted = rfq.Quotes.Select(q => q.SupplierId).Distinct().Count();
            Log(rfq, $"截止報價，進入比價（{quoted}/{rfq.InvitedSupplierIds.Count} 家已報價）");
        }
    }

    /// <summary>對供應商提出議價（還價）</summary>
    public void Negotiate(int rfqId, int supplierId, Dictionary<int, decimal> counterPrices, string message)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status is RfqStatus.Comparing or RfqStatus.Negotiating, "只有比價中或議價中可以議價");
            var latest = LatestQuote(rfq, supplierId);
            Require(latest != null, "該供應商尚未報價");
            Require(!rfq.Negotiations.Any(n => n.SupplierId == supplierId && !n.Replied), "已有一筆議價等待供應商回覆");
            rfq.Negotiations.Add(new NegotiationRequest
            {
                Id = rfq.Negotiations.Count + 1,
                SupplierId = supplierId,
                Round = latest!.Round + 1,
                CreatedAt = Now,
                Message = message,
                CounterPrices = counterPrices
            });
            rfq.Status = RfqStatus.Negotiating;
            Log(rfq, $"向 {Supplier(supplierId).Name} 發起第 {latest.Round + 1} 輪議價：{message}");
        }
    }

    /// <summary>模擬供應商回覆議價：通常會讓步一部分，不一定完全接受還價</summary>
    public void SimulateNegotiationReply(int rfqId, int negotiationId)
    {
        var rfq = Get(rfqId);
        var neg = rfq.Negotiations.First(n => n.Id == negotiationId);
        Require(!neg.Replied, "此議價已回覆");
        var latest = LatestQuote(rfq, neg.SupplierId)!;
        var accept = _rand.NextDouble();
        neg.Reply = accept > 0.75 ? "同意貴司還價" : accept > 0.3 ? "原物料上漲，只能部分讓步" : "已是最低價，僅能小幅調整";
        SubmitQuote(rfqId, new Quotation
        {
            SupplierId = neg.SupplierId,
            PaymentTerms = latest.PaymentTerms,
            Remark = neg.Reply,
            Lines = latest.Lines.Select(l =>
            {
                var counter = neg.CounterPrices.TryGetValue(l.RfqLineId, out var c) ? c : l.UnitPrice;
                var gap = l.UnitPrice - counter;
                var ratio = accept > 0.75 ? 1m : accept > 0.3 ? 0.5m + (decimal)_rand.NextDouble() * 0.3m : 0.2m;
                var price = gap > 0 ? Math.Round(l.UnitPrice - gap * ratio, 1) : l.UnitPrice;
                var qty = rfq.Lines.First(x => x.Id == l.RfqLineId).Qty;
                return new QuoteLine { RfqLineId = l.RfqLineId, UnitPrice = price, LeadTimeDays = l.LeadTimeDays, Moq = l.Moq, Tiers = ScaleTiers(l, price, qty) };
            }).ToList()
        });
    }

    /// <summary>送出決標（每個項目選一家供應商），並依金額產生簽核流程</summary>
    public void SubmitAward(int rfqId, Dictionary<int, int> lineToSupplier, string reason)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status is RfqStatus.Comparing or RfqStatus.Negotiating, "目前狀態不可決標");
            Require(!rfq.Negotiations.Any(n => !n.Replied), "仍有議價等待供應商回覆，請先處理");
            Require(rfq.Lines.All(l => lineToSupplier.ContainsKey(l.Id)), "每個項目都必須選擇得標供應商");
            rfq.Awards = rfq.Lines.Select(l =>
            {
                var sid = lineToSupplier[l.Id];
                var ql = LatestQuote(rfq, sid)?.Lines.FirstOrDefault(x => x.RfqLineId == l.Id);
                Require(ql != null, $"{Supplier(sid).Name} 沒有報價項次 {l.Id}");
                return new Award { RfqLineId = l.Id, SupplierId = sid, Qty = l.Qty, UnitPrice = ql!.UnitPrice };
            }).ToList();

            // 若沒選最低價，必須填寫理由（常見內控要求）
            var notLowest = rfq.Lines.Where(l =>
            {
                var min = rfq.InvitedSupplierIds.Select(s => LatestQuote(rfq, s)?.Lines.FirstOrDefault(x => x.RfqLineId == l.Id)?.UnitPrice)
                    .Where(p => p.HasValue).Min();
                return rfq.Awards.First(a => a.RfqLineId == l.Id).UnitPrice > min;
            }).ToList();
            Require(notLowest.Count == 0 || !string.IsNullOrWhiteSpace(reason), "有項目未決標給最低價，請填寫決標理由");
            rfq.AwardReason = reason;

            var total = rfq.AwardTotal;
            rfq.Approvals = ApprovalMatrix.Where(m => total >= m.Threshold)
                .Select((m, i) => new ApprovalStep { Seq = i + 1, Role = m.Role, Approver = m.Approver, Status = i == 0 ? "Pending" : "Waiting" })
                .ToList();
            rfq.Status = RfqStatus.PendingApproval;
            Log(rfq, $"送出決標簽核，決標金額 {total:N0}，需簽核：{string.Join(" → ", rfq.Approvals.Select(a => a.Role))}");
        }
    }

    public void Approve(int rfqId, bool approve, string comment)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status == RfqStatus.PendingApproval, "目前不在簽核中");
            var step = rfq.Approvals.First(a => a.Status == "Pending");
            step.Status = approve ? "Approved" : "Rejected";
            step.Comment = comment;
            step.At = Now;
            var who = $"{step.Approver}（{step.Role}）";
            if (!approve)
            {
                rfq.Status = RfqStatus.Comparing;
                rfq.Awards.Clear();
                Log(rfq, $"退回決標：{comment}", who);
                return;
            }
            Log(rfq, $"核准決標{(string.IsNullOrEmpty(comment) ? "" : "：" + comment)}", who);
            var next = rfq.Approvals.FirstOrDefault(a => a.Status == "Waiting");
            if (next != null) next.Status = "Pending";
            else
            {
                rfq.Status = RfqStatus.Awarded;
                Log(rfq, "簽核完成，正式決標", "系統");
            }
        }
    }

    /// <summary>決標後轉採購單：依得標供應商拆成多張 PO，並寫入歷史價格</summary>
    public List<PurchaseOrder> CreatePurchaseOrders(int rfqId)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status == RfqStatus.Awarded, "需簽核完成才能轉採購單");
            var result = new List<PurchaseOrder>();
            foreach (var g in rfq.Awards.GroupBy(a => a.SupplierId))
            {
                var id = PurchaseOrders.Count + 1;
                var po = new PurchaseOrder
                {
                    Id = id,
                    No = $"PO-{Now:yyyyMM}-{id:000}",
                    RfqId = rfq.Id,
                    DeliveryPlace = rfq.DeliveryPlace,
                    SupplierId = g.Key,
                    CreatedAt = Now,
                    PaymentTerms = LatestQuote(rfq, g.Key)?.PaymentTerms ?? Supplier(g.Key).PaymentTerms,
                    Lines = g.Select(a =>
                    {
                        var line = rfq.Lines.First(l => l.Id == a.RfqLineId);
                        return new PoLine { MaterialId = line.MaterialId, Qty = a.Qty, UnitPrice = a.UnitPrice, DeliveryDate = line.RequiredDate };
                    }).ToList()
                };
                PurchaseOrders.Add(po);
                result.Add(po);
                foreach (var l in po.Lines)
                    PriceHistories.Add(new PriceHistory { MaterialId = l.MaterialId, SupplierId = g.Key, Date = Now, UnitPrice = l.UnitPrice, Qty = l.Qty, Source = rfq.No });
            }
            rfq.Status = RfqStatus.PoCreated;
            Log(rfq, "轉採購單：" + string.Join("、", result.Select(p => p.No)));
            return result;
        }
    }

    /// <summary>供應商在入口網站確認接單</summary>
    public void ConfirmPo(int poId, int supplierId)
    {
        lock (_lock)
        {
            var po = PurchaseOrders.FirstOrDefault(p => p.Id == poId && p.SupplierId == supplierId) ?? throw new BusinessException("找不到採購單");
            Require(po.Status == "已發出", "此採購單已確認");
            po.Status = "供應商已確認";
            po.ConfirmedAt = Now;
            var rfq = FindRfq(po.RfqId);
            if (rfq != null) Log(rfq, $"{Supplier(supplierId).Name} 確認接單 {po.No}", Supplier(supplierId).Name);
            var ct = Contracts.FirstOrDefault(c => c.Id == po.ContractId);
            if (ct != null) CLog(ct, $"{Supplier(supplierId).Name} 確認接單 {po.No}", Supplier(supplierId).Name);
        }
    }

    // ------------------------------------------------------------------
    // 合約價管理
    // ------------------------------------------------------------------

    private void CLog(PriceContract c, string action, string? user = null) =>
        c.Logs.Add(new AuditLog { At = Now, User = user ?? CurrentUser, Action = action });

    public PriceContract? FindContract(int id) => Contracts.FirstOrDefault(c => c.Id == id);

    /// <summary>料件目前生效中的合約（可能有多家供應商）</summary>
    public List<PriceContract> ActiveContracts(int materialId) =>
        Contracts.Where(c => c.MaterialId == materialId && c.IsActive).OrderBy(c => c.UnitPrice).ToList();

    /// <summary>合約已使用量＝依此合約下單的採購數量合計</summary>
    public decimal ContractUsed(PriceContract c) =>
        PurchaseOrders.Where(p => p.ContractId == c.Id).SelectMany(p => p.Lines).Sum(l => l.Qty);

    public decimal ContractSpend(PriceContract c) =>
        PurchaseOrders.Where(p => p.ContractId == c.Id).Sum(p => p.Total);

    /// <summary>
    /// 由決標結果建立合約價：每個決標項目一張合約，沿用最後一輪報價的單價、階梯、交期與付款條件。
    /// 同一供應商、同一料件若已有生效中的合約，舊合約會提前結束、由新合約取代。
    /// </summary>
    public List<PriceContract> CreateContractsFromRfq(int rfqId, DateTime validFrom, DateTime validTo, Dictionary<int, decimal> committedQty)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status is RfqStatus.Awarded or RfqStatus.PoCreated, "需決標簽核完成才能建立合約價");
            Require(validTo > validFrom, "合約結束日必須晚於開始日");
            var result = new List<PriceContract>();
            foreach (var a in rfq.Awards)
            {
                var line = rfq.Lines.First(l => l.Id == a.RfqLineId);
                Require(!Contracts.Any(c => c.SourceRfqId == rfq.Id && c.MaterialId == line.MaterialId && !c.Terminated),
                    $"{Material(line.MaterialId).PartNo} 已由此詢價單建立過合約");
                var q = LatestQuote(rfq, a.SupplierId)!;
                var ql = q.Lines.First(l => l.RfqLineId == a.RfqLineId);
                var c = new PriceContract
                {
                    Id = Contracts.Count == 0 ? 1 : Contracts.Max(x => x.Id) + 1,
                    SupplierId = a.SupplierId,
                    MaterialId = line.MaterialId,
                    UnitPrice = a.UnitPrice,
                    Tiers = ql.Tiers.Select(t => new PriceTier { MinQty = t.MinQty, UnitPrice = t.UnitPrice }).ToList(),
                    ValidFrom = validFrom.Date,
                    ValidTo = validTo.Date,
                    CommittedQty = committedQty.GetValueOrDefault(a.RfqLineId),
                    Moq = ql.Moq,
                    LeadTimeDays = ql.LeadTimeDays,
                    PaymentTerms = q.PaymentTerms,
                    SourceRfqId = rfq.Id,
                    CreatedAt = Now
                };
                c.No = $"CT-{Now:yyyy}-{c.Id:000}";
                // 取代舊合約
                foreach (var old in Contracts.Where(o => o.SupplierId == c.SupplierId && o.MaterialId == c.MaterialId && o.IsActive && o.ValidTo >= c.ValidFrom))
                {
                    old.ValidTo = c.ValidFrom.AddDays(-1);
                    c.RenewedFromId = old.Id;
                    CLog(old, $"由新合約 {c.No} 取代，有效期提前至 {old.ValidTo:yyyy/MM/dd}");
                }
                Contracts.Add(c);
                CLog(c, $"由 {rfq.No} 決標結果建立合約，有效期 {c.ValidFrom:yyyy/MM/dd} ~ {c.ValidTo:yyyy/MM/dd}，單價 {c.UnitPrice:N2}");
                result.Add(c);
            }
            Log(rfq, "建立合約價：" + string.Join("、", result.Select(c => c.No)));
            return result;
        }
    }

    /// <summary>依合約下單（Release Order）：直接以合約價開採購單，不需重新詢價</summary>
    public (PurchaseOrder Po, string? Warning) ReleaseOrder(int contractId, decimal qty, DateTime deliveryDate, string deliveryPlace)
    {
        lock (_lock)
        {
            var c = FindContract(contractId) ?? throw new BusinessException("找不到合約");
            Require(!c.Terminated && Now.Date >= c.ValidFrom && Now.Date <= c.ValidTo, $"合約狀態為「{c.Status}」，無法依合約下單");
            Require(qty > 0, "請輸入下單數量");
            Require(qty >= c.Moq, $"下單數量低於最小訂購量 {c.Moq:N0}");
            Require(deliveryDate.Date <= c.ValidTo.Date.AddDays(90), "交貨日距合約到期太久，請確認");
            var id = PurchaseOrders.Count + 1;
            var price = c.PriceAt(qty);
            var po = new PurchaseOrder
            {
                Id = id,
                No = $"PO-{Now:yyyyMM}-{id:000}",
                ContractId = c.Id,
                SupplierId = c.SupplierId,
                CreatedAt = Now,
                PaymentTerms = c.PaymentTerms,
                DeliveryPlace = string.IsNullOrWhiteSpace(deliveryPlace) ? "新竹廠" : deliveryPlace,
                Lines = { new PoLine { MaterialId = c.MaterialId, Qty = qty, UnitPrice = price, DeliveryDate = deliveryDate.Date } }
            };
            PurchaseOrders.Add(po);
            PriceHistories.Add(new PriceHistory { MaterialId = c.MaterialId, SupplierId = c.SupplierId, Date = Now, UnitPrice = price, Qty = qty, Source = c.No });
            CLog(c, $"依合約下單 {po.No}：{qty:N0} × {price:N2} = {po.Total:N0}");
            string? warning = null;
            if (c.CommittedQty > 0)
            {
                var used = ContractUsed(c);
                if (used > c.CommittedQty) warning = $"注意：累計下單量 {used:N0} 已超過合約承諾量 {c.CommittedQty:N0}，超出部分建議與供應商確認價格是否仍適用。";
            }
            if (c.Tiers.Count > 0 && c.Tiers.FirstOrDefault(t => t.MinQty > qty) is { } next)
                warning = (warning == null ? "" : warning + " ") + $"提示：數量達 {next.MinQty:N0} 可適用單價 {next.UnitPrice:N2}。";
            return (po, warning);
        }
    }

    public void TerminateContract(int contractId, string reason)
    {
        lock (_lock)
        {
            var c = FindContract(contractId) ?? throw new BusinessException("找不到合約");
            Require(!c.Terminated && c.Status != "已到期", "此合約已結束");
            Require(!string.IsNullOrWhiteSpace(reason), "請填寫終止原因");
            c.Terminated = true;
            c.TerminateReason = reason;
            CLog(c, "終止合約：" + reason);
        }
    }

    /// <summary>續約詢價：以合約內容建立一張詢價草稿，目標價帶入現行合約價，並邀請原供應商與同分類合格供應商</summary>
    public Rfq CreateRenewalRfq(int contractId)
    {
        var c = FindContract(contractId) ?? throw new BusinessException("找不到合約");
        var m = Material(c.MaterialId);
        var used = ContractUsed(c);
        var rfq = CreateRfq(new Rfq
        {
            Title = $"{m.Name} 合約續約詢價",
            Department = "採購部",
            Deadline = Now.Date.AddDays(10),
            Remark = $"現行合約 {c.No} 將於 {c.ValidTo:yyyy/MM/dd} 到期，請報價新年度價格",
            Lines = { new RfqLine { MaterialId = c.MaterialId, Qty = c.CommittedQty > 0 ? c.CommittedQty : Math.Max(used, c.Moq), TargetPrice = c.UnitPrice, RequiredDate = c.ValidTo.AddDays(14) } }
        });
        rfq.InvitedSupplierIds = Suppliers.Where(s => s.IsQualified && (s.Id == c.SupplierId || s.Categories.Contains(m.Category))).Select(s => s.Id).ToList();
        Log(rfq, $"由合約 {c.No} 發起續約詢價，已預先邀請 {rfq.InvitedSupplierIds.Count} 家供應商");
        CLog(c, $"發起續約詢價 {rfq.No}");
        return rfq;
    }

    /// <summary>從供應商角度看這張詢價單的狀態（供應商看不到別家報價與目標價）</summary>
    public (string Text, string Badge, bool NeedAction) PortalStatus(Rfq rfq, int sid)
    {
        var latest = LatestQuote(rfq, sid);
        if (rfq.Status == RfqStatus.Cancelled) return ("已取消", "secondary", false);
        if (rfq.Status is RfqStatus.Awarded or RfqStatus.PoCreated)
            return rfq.Awards.Any(a => a.SupplierId == sid) ? ("得標", "success", false) : ("未得標", "secondary", false);
        if (rfq.Negotiations.Any(n => n.SupplierId == sid && !n.Replied)) return ("議價待回覆", "danger", true);
        if (rfq.Declines.ContainsKey(sid)) return ("已婉拒", "secondary", false);
        if (rfq.Status == RfqStatus.Quoting && !IsPastDeadline(rfq))
            return latest == null ? ("待報價", "danger", true) : ("已報價（截止前可修改）", "primary", false);
        return latest == null ? ("未報價", "secondary", false) : ("採購評估中", "info", false);
    }

    public void Cancel(int rfqId, string reason)
    {
        lock (_lock)
        {
            var rfq = Get(rfqId);
            Require(rfq.Status is not (RfqStatus.PoCreated or RfqStatus.Cancelled), "此狀態不可取消");
            rfq.Status = RfqStatus.Cancelled;
            Log(rfq, "取消詢價單：" + reason);
        }
    }

    private Rfq Get(int id) => FindRfq(id) ?? throw new BusinessException("找不到詢價單");

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new BusinessException(message);
    }

    // ------------------------------------------------------------------
    // 假資料
    // ------------------------------------------------------------------

    private void Seed()
    {
        Suppliers.AddRange(new[]
        {
            new Supplier { Id = 1, Code = "S001", Name = "台灣精密金屬股份有限公司", Contact = "陳建宏", Phone = "03-5551234", Email = "sales@twprecision.example", Categories = new() { "金屬加工", "螺絲五金" }, QualityScore = 92, OnTimeRate = 95, PaymentTerms = "月結60天" },
            new Supplier { Id = 2, Code = "S002", Name = "宏達電子零件有限公司", Contact = "林佩君", Phone = "02-2771888", Email = "rfq@hongda.example", Categories = new() { "電子零件" }, QualityScore = 88, OnTimeRate = 90, PaymentTerms = "月結30天" },
            new Supplier { Id = 3, Code = "S003", Name = "永豐包材企業社", Contact = "黃志強", Phone = "04-2345678", Email = "yf.pack@example.com", Categories = new() { "包材" }, QualityScore = 75, OnTimeRate = 82, PaymentTerms = "月結30天" },
            new Supplier { Id = 4, Code = "S004", Name = "聯成機電股份有限公司", Contact = "張家豪", Phone = "03-4567890", Email = "bid@liancheng.example", Categories = new() { "金屬加工", "電子零件" }, QualityScore = 85, OnTimeRate = 78, PaymentTerms = "月結90天" },
            new Supplier { Id = 5, Code = "S005", Name = "大興五金行", Contact = "吳大興", Phone = "06-2223333", Email = "daxing@example.com", Categories = new() { "螺絲五金" }, QualityScore = 70, OnTimeRate = 88, PaymentTerms = "貨到付款" },
            new Supplier { Id = 6, Code = "S006", Name = "全球電子科技股份有限公司", Contact = "許雅雯", Phone = "02-8797999", Email = "sales@globalelec.example", Categories = new() { "電子零件" }, QualityScore = 95, OnTimeRate = 97, PaymentTerms = "月結60天" },
            new Supplier { Id = 7, Code = "S007", Name = "綠能包裝股份有限公司", Contact = "蔡宗翰", Phone = "07-3334444", Email = "green.pack@example.com", Categories = new() { "包材" }, QualityScore = 83, OnTimeRate = 91, PaymentTerms = "月結60天" },
            new Supplier { Id = 8, Code = "S008", Name = "新創塑膠工業", Contact = "鄭美華", Phone = "04-7778888", Email = "newplastic@example.com", Categories = new() { "塑膠射出" }, QualityScore = 65, OnTimeRate = 70, PaymentTerms = "月結30天", IsQualified = false },
            new Supplier { Id = 9, Code = "S009", Name = "鼎盛塑膠股份有限公司", Contact = "邱文彬", Phone = "03-3216549", Email = "ds.plastic@example.com", Categories = new() { "塑膠射出" }, QualityScore = 87, OnTimeRate = 89, PaymentTerms = "月結60天" },
        });

        Materials.AddRange(new[]
        {
            new Material { Id = 1, PartNo = "M-1001", Name = "鋁合金外殼", Spec = "6061-T6 200x120x40mm 陽極處理", Category = "金屬加工" },
            new Material { Id = 2, PartNo = "M-1002", Name = "不鏽鋼支架", Spec = "SUS304 t=2.0mm 折彎", Category = "金屬加工" },
            new Material { Id = 3, PartNo = "E-2001", Name = "電源供應器", Spec = "24V 5A 120W UL認證", Category = "電子零件" },
            new Material { Id = 4, PartNo = "E-2002", Name = "控制板 PCBA", Spec = "4層板 含SMT組裝", Category = "電子零件" },
            new Material { Id = 5, PartNo = "E-2003", Name = "液晶顯示模組", Spec = "7吋 1024x600 IPS", Category = "電子零件" },
            new Material { Id = 6, PartNo = "H-3001", Name = "六角螺絲", Spec = "M6x20 SUS304（1000pcs/盒）", Unit = "盒", Category = "螺絲五金" },
            new Material { Id = 7, PartNo = "H-3002", Name = "彈簧墊圈", Spec = "M6 SUS304（1000pcs/盒）", Unit = "盒", Category = "螺絲五金" },
            new Material { Id = 8, PartNo = "P-4001", Name = "瓦楞紙箱", Spec = "A楞 450x350x300mm", Category = "包材" },
            new Material { Id = 9, PartNo = "P-4002", Name = "防靜電袋", Spec = "200x300mm 夾鏈", Category = "包材" },
            new Material { Id = 10, PartNo = "R-5001", Name = "塑膠前蓋", Spec = "ABS 黑色 射出成型", Category = "塑膠射出" },
        });

        // 過去 12 個月的歷史成交價
        var basePrices = new Dictionary<int, (decimal Price, int[] Suppliers, decimal Qty)>
        {
            [1] = (480, new[] { 1, 4 }, 500), [2] = (85, new[] { 1, 4 }, 1000), [3] = (650, new[] { 2, 6 }, 300),
            [4] = (1250, new[] { 6, 4 }, 300), [5] = (1800, new[] { 6, 2 }, 100), [6] = (420, new[] { 5, 1 }, 20),
            [7] = (180, new[] { 5, 1 }, 20), [8] = (32, new[] { 3, 7 }, 3000), [9] = (4.5m, new[] { 7, 3 }, 10000),
            [10] = (55, new[] { 9 }, 2000),
        };
        var today = DateTime.Today;
        foreach (var (mid, info) in basePrices)
            for (var m = 12; m >= 1; m -= 2)
            {
                var drift = 1 + (decimal)Math.Sin(m / 2.0 + mid) * 0.06m;
                PriceHistories.Add(new PriceHistory
                {
                    MaterialId = mid,
                    SupplierId = info.Suppliers[m / 2 % info.Suppliers.Length],
                    Date = today.AddMonths(-m).AddDays(mid),
                    UnitPrice = Math.Round(info.Price * drift, 1),
                    Qty = info.Qty,
                    Source = "歷史採購"
                });
            }

        // 用真正的業務流程產生不同狀態的詢價單，讓每個階段都有範例可以看
        DateTime At(int daysAgo, int hour = 10) => today.AddDays(-daysAgo).AddHours(hour);
        RfqLine L(int mat, decimal qty, decimal target, int reqDays) =>
            new() { MaterialId = mat, Qty = qty, TargetPrice = target, RequiredDate = today.AddDays(reqDays) };
        Quotation Q(int sid, params (decimal Price, int Lead, decimal Moq)[] lines) =>
            new() { SupplierId = sid, Source = "入口網站", Lines =lines.Select((l, i) => new QuoteLine { RfqLineId = i + 1, UnitPrice = l.Price, LeadTimeDays = l.Lead, Moq = l.Moq }).ToList() };
        // 為報價的某個項目加上階梯價（數量, 單價）
        Quotation T(Quotation q, int lineId, params (decimal Qty, decimal Price)[] tiers)
        {
            q.Lines.First(l => l.RfqLineId == lineId).Tiers = tiers.Select(t => new PriceTier { MinQty = t.Qty, UnitPrice = t.Price }).ToList();
            return q;
        }

        // 1. 已轉採購單（完整走完流程）
        _clock = At(45);
        var r1 = CreateRfq(new Rfq { Title = "第一季控制器零件採購", Department = "生產管理部", Deadline = today.AddDays(-38), Lines = { L(3, 300, 640, -10), L(4, 300, 1200, -10) } });
        InviteSuppliers(r1.Id, new() { 2, 4, 6 });
        Send(r1.Id);
        _clock = At(41); SubmitQuote(r1.Id, Q(2, (690, 21, 100), (1320, 28, 100)));
        _clock = At(40); SubmitQuote(r1.Id, Q(4, (665, 35, 200), (1280, 35, 150)));
        _clock = At(39); SubmitQuote(r1.Id, Q(6, (672, 14, 50), (1295, 21, 50)));
        _clock = At(38); OpenBids(r1.Id, "周建國（稽核室）");
        _clock = At(37); Negotiate(r1.Id, 6, new() { [1] = 640, [2] = 1220 }, "貴司品質與交期最佳，若價格能接近目標價將全數下單");
        _clock = At(36); SubmitQuote(r1.Id, new Quotation { SupplierId = 6, Remark = "考量長期合作，同意部分讓步", Lines = { new() { RfqLineId = 1, UnitPrice = 648, LeadTimeDays = 14, Moq = 50 }, new() { RfqLineId = 2, UnitPrice = 1235, LeadTimeDays = 21, Moq = 50 } } });
        _clock = At(35); SubmitAward(r1.Id, new() { [1] = 6, [2] = 6 }, "全球電子品質評等 A、交期最短，議價後總價與最低價差距 < 1%");
        _clock = At(34); Approve(r1.Id, true, "同意");
        _clock = At(33); Approve(r1.Id, true, "價格合理");
        _clock = At(32); CreatePurchaseOrders(r1.Id);

        // 2. 簽核中（分批決標：兩個項目給不同供應商）
        _clock = At(20);
        var r2 = CreateRfq(new Rfq { Title = "機殼加工件年度詢價", Department = "研發一部", Deadline = today.AddDays(-12), Remark = "需附材質證明與尺寸檢驗報告", Lines = { L(1, 2000, 460, 30), L(2, 4000, 80, 30) } });
        InviteSuppliers(r2.Id, new() { 1, 4 });
        Send(r2.Id);
        _clock = At(15); SubmitQuote(r2.Id, Q(1, (472, 28, 500), (88, 21, 1000)));
        _clock = At(14); SubmitQuote(r2.Id, Q(4, (495, 35, 500), (79.5m, 28, 2000)));
        _clock = At(12); OpenBids(r2.Id, "周建國（稽核室）");
        _clock = At(11); Negotiate(r2.Id, 1, new() { [1] = 455, [2] = 80 }, "年度用量 2000 套，請提供量價優惠");
        _clock = At(9); SubmitQuote(r2.Id, new Quotation { SupplierId = 1, Remark = "外殼可降價，支架因鋼材上漲無法調整", Lines = { new() { RfqLineId = 1, UnitPrice = 458, LeadTimeDays = 28, Moq = 500 }, new() { RfqLineId = 2, UnitPrice = 88, LeadTimeDays = 21, Moq = 1000 } } });
        _clock = At(3); SubmitAward(r2.Id, new() { [1] = 1, [2] = 4 }, "");
        _clock = At(2); Approve(r2.Id, true, "分批決標可降低單一供應商風險，同意");

        // 3. 議價中（等待供應商回覆）
        _clock = At(12);
        var r3 = CreateRfq(new Rfq { Title = "包材第四季詢價", Department = "倉儲物流部", Deadline = today.AddDays(-4), Lines = { L(8, 6000, 30, 20), L(9, 20000, 4.2m, 20) } });
        InviteSuppliers(r3.Id, new() { 3, 7 });
        Send(r3.Id);
        _clock = At(8); SubmitQuote(r3.Id, T(T(Q(3, (31.5m, 10, 1000), (4.6m, 7, 5000)), 1, (1000, 33), (5000, 31.5m), (10000, 30.2m)), 2, (5000, 4.8m), (20000, 4.6m), (50000, 4.3m)));
        _clock = At(7); SubmitQuote(r3.Id, T(Q(7, (33, 7, 500), (4.3m, 7, 5000)), 1, (500, 34.5m), (5000, 33), (10000, 29.8m)));
        _clock = At(4); OpenBids(r3.Id, "陳美玲（財務協理）");
        _clock = At(1); Negotiate(r3.Id, 7, new() { [1] = 30.5m, [2] = 4.2m }, "紙箱價格高於他廠，請重新評估");

        // 4. 比價中（報價已截止）
        _clock = At(10);
        var r4 = CreateRfq(new Rfq { Title = "螺絲五金補料", Department = "生產管理部", Deadline = today.AddDays(-1), Lines = { L(6, 30, 410, 14), L(7, 30, 175, 14) } });
        InviteSuppliers(r4.Id, new() { 1, 5 });
        Send(r4.Id);
        _clock = At(5); SubmitQuote(r4.Id, T(Q(5, (398, 7, 10), (182, 7, 10)), 1, (10, 410), (30, 398), (50, 392)));
        _clock = At(3); SubmitQuote(r4.Id, T(Q(1, (425, 14, 5), (170, 14, 5)), 1, (5, 440), (30, 425), (100, 380)));
        _clock = At(1); OpenBids(r4.Id, "周建國（稽核室）");

        // 5. 詢價中・密封（只有一家投標、一家婉拒，採購看不到價格）
        _clock = At(4);
        var r5 = CreateRfq(new Rfq { Title = "新機種顯示模組詢價", Department = "研發二部", Deadline = today.AddDays(3), Remark = "請一併報價樣品費與 NRE 費用", Lines = { L(5, 500, 1750, 45), L(4, 500, 1230, 45) } });
        InviteSuppliers(r5.Id, new() { 2, 4, 6 });
        Send(r5.Id);
        _clock = At(1); SubmitQuote(r5.Id, Q(6, (1820, 30, 100), (1265, 21, 100)));
        _clock = At(1, 15); DeclineQuote(r5.Id, 4, "顯示模組非本公司供應範圍");

        // 6. 草稿
        _clock = At(0, 9);
        CreateRfq(new Rfq { Title = "塑膠前蓋開發詢價", Department = "研發一部", Deadline = today.AddDays(10), Remark = "需另報模具費", Lines = { L(10, 5000, 52, 60) } });

        const string Audit = "周建國（稽核室）";

        // 7. 簽核中・三層簽核（金額 ≥ 200 萬需總經理核准），目前在財務協理
        _clock = At(25);
        var r7 = CreateRfq(new Rfq { Title = "伺服器機殼大量採購", Department = "生產管理部", Deadline = today.AddDays(-19), Remark = "分三批交貨", Lines = { L(1, 5000, 465, 40) } });
        InviteSuppliers(r7.Id, new() { 1, 4 });
        Send(r7.Id);
        _clock = At(21); SubmitQuote(r7.Id, Q(1, (478, 28, 500)));
        _clock = At(20); SubmitQuote(r7.Id, Q(4, (470, 35, 1000)));
        _clock = At(18); OpenBids(r7.Id, Audit);
        _clock = At(17); Negotiate(r7.Id, 1, new() { [1] = 462 }, "聯成報價較低，貴司若能降至 462 將優先考慮");
        _clock = At(15); SubmitQuote(r7.Id, new Quotation { SupplierId = 1, Source = "入口網站", Remark = "量大可配合降價", Lines = { new() { RfqLineId = 1, UnitPrice = 466, LeadTimeDays = 28, Moq = 500 } } });
        _clock = At(4); SubmitAward(r7.Id, new() { [1] = 1 }, "");
        _clock = At(3); Approve(r7.Id, true, "量大，請財務確認付款條件");

        // 8. 簽核中・未決標給最低價（需填理由），目前在採購主管
        _clock = At(16);
        var r8 = CreateRfq(new Rfq { Title = "電源供應器年度合約", Department = "生產管理部", Deadline = today.AddDays(-10), Lines = { L(3, 1000, 640, 30) } });
        InviteSuppliers(r8.Id, new() { 2, 4, 6 });
        Send(r8.Id);
        _clock = At(13); SubmitQuote(r8.Id, Q(2, (655, 21, 100)));
        _clock = At(12); SubmitQuote(r8.Id, T(Q(4, (628, 42, 200)), 1, (200, 640), (1000, 628), (3000, 605)));
        _clock = At(12, 15); SubmitQuote(r8.Id, Q(6, (662, 14, 50)));
        _clock = At(9); OpenBids(r8.Id, Audit);
        _clock = At(1); SubmitAward(r8.Id, new() { [1] = 6 }, "最低價的聯成機電準交率僅 78%、交期 42 天無法配合量產；全球電子品質 A 級、交期 14 天，價差 5.4% 可接受");

        // 9. 簽核中・公開報價、小金額只需一層簽核
        _clock = At(6);
        var r9 = CreateRfq(new Rfq { Title = "防靜電袋緊急補料", Department = "倉儲物流部", Deadline = today.AddDays(-4), SealedBid = false, Remark = "緊急，請於一週內交貨", Lines = { L(9, 30000, 4.3m, 7) } });
        InviteSuppliers(r9.Id, new() { 3, 7 });
        Send(r9.Id);
        _clock = At(5); SubmitQuote(r9.Id, Q(7, (4.25m, 5, 5000)));
        _clock = At(5, 14); SubmitQuote(r9.Id, Q(3, (4.4m, 7, 5000)));
        _clock = At(4); CloseQuoting(r9.Id);
        _clock = At(0, 9); SubmitAward(r9.Id, new() { [1] = 7 }, "");

        // 10. 簽核被退回，回到比價中
        _clock = At(14);
        var r10 = CreateRfq(new Rfq { Title = "控制板 PCBA 第二來源", Department = "研發二部", Deadline = today.AddDays(-8), Lines = { L(4, 800, 1210, 45) } });
        InviteSuppliers(r10.Id, new() { 2, 4, 6 });
        Send(r10.Id);
        _clock = At(11); SubmitQuote(r10.Id, Q(2, (1290, 28, 100)));
        _clock = At(10); SubmitQuote(r10.Id, Q(4, (1245, 35, 200)));
        _clock = At(10, 16); SubmitQuote(r10.Id, Q(6, (1260, 21, 100)));
        _clock = At(8); OpenBids(r10.Id, Audit);
        _clock = At(5); SubmitAward(r10.Id, new() { [1] = 4 }, "");
        _clock = At(4); Approve(r10.Id, false, "聯成準交率偏低，請與全球電子再議價後重新送簽");

        // 11. 待開標：已過截止時間
        _clock = At(9);
        var r11 = CreateRfq(new Rfq { Title = "螺絲年度合約詢價", Department = "生產管理部", Deadline = today.AddDays(-1), Lines = { L(6, 240, 405, 30), L(7, 240, 172, 30) } });
        InviteSuppliers(r11.Id, new() { 1, 5 });
        Send(r11.Id);
        _clock = At(5); SubmitQuote(r11.Id, Q(5, (395, 7, 10), (176, 7, 10)));
        _clock = At(3); SubmitQuote(r11.Id, Q(1, (418, 14, 5), (168, 14, 5)));

        // 12. 待開標：未到截止，但所有廠商都已回覆，可提前開標
        _clock = At(3);
        var r12 = CreateRfq(new Rfq { Title = "液晶模組第二來源詢價", Department = "研發二部", Deadline = today.AddDays(5), Lines = { L(5, 1200, 1700, 60) } });
        InviteSuppliers(r12.Id, new() { 2, 4, 6 });
        Send(r12.Id);
        _clock = At(2); SubmitQuote(r12.Id, Q(6, (1760, 30, 100)));
        _clock = At(1); SubmitQuote(r12.Id, T(Q(2, (1735, 35, 200)), 1, (200, 1790), (1000, 1735), (2000, 1690)));
        _clock = At(1, 14); DeclineQuote(r12.Id, 4, "產能已滿");

        // 13. 詢價中・公開報價（對照組：採購在截止前就看得到價格）
        _clock = At(2);
        var r13 = CreateRfq(new Rfq { Title = "紙箱季度詢價（公開報價）", Department = "倉儲物流部", Deadline = today.AddDays(4), SealedBid = false, Lines = { L(8, 8000, 30, 25) } });
        InviteSuppliers(r13.Id, new() { 3, 7 });
        Send(r13.Id);
        _clock = At(1); SubmitQuote(r13.Id, T(Q(3, (30.8m, 10, 1000)), 1, (1000, 33), (5000, 31.5m), (8000, 30.8m), (15000, 29.5m)));

        // 14. 已決標，待轉採購單
        _clock = At(13);
        var r14 = CreateRfq(new Rfq { Title = "不鏽鋼支架追加", Department = "研發一部", Deadline = today.AddDays(-9), Lines = { L(2, 3000, 82, 20) } });
        InviteSuppliers(r14.Id, new() { 1, 4 });
        Send(r14.Id);
        _clock = At(10); SubmitQuote(r14.Id, Q(1, (86, 21, 1000)));
        _clock = At(9); SubmitQuote(r14.Id, Q(4, (81, 28, 2000)));
        _clock = At(8); OpenBids(r14.Id, Audit);
        _clock = At(6); SubmitAward(r14.Id, new() { [1] = 4 }, "");
        _clock = At(5); Approve(r14.Id, true, "同意");

        // 15. 已取消
        _clock = At(18);
        var r15 = CreateRfq(new Rfq { Title = "塑膠樣品詢價", Department = "研發一部", Deadline = today.AddDays(-11), Lines = { L(10, 200, 60, 30) } });
        InviteSuppliers(r15.Id, new() { 9 });
        Send(r15.Id);
        _clock = At(16); Cancel(r15.Id, "研發專案暫停，需求取消");

        // ===== 合約價 =====
        // 系統上線前就存在的年度合約（不是由詢價單產生）
        PriceContract Legacy(int sid, int mid, decimal price, int fromDaysAgo, int toDaysLater, decimal committed, decimal moq, int lead, params (decimal Qty, decimal Price)[] tiers)
        {
            var c = new PriceContract
            {
                Id = Contracts.Count + 1, SupplierId = sid, MaterialId = mid, UnitPrice = price,
                Tiers = tiers.Select(t => new PriceTier { MinQty = t.Qty, UnitPrice = t.Price }).ToList(),
                ValidFrom = today.AddDays(-fromDaysAgo), ValidTo = today.AddDays(toDaysLater), CommittedQty = committed,
                Moq = moq, LeadTimeDays = lead, PaymentTerms = Supplier(sid).PaymentTerms,
                CreatedAt = today.AddDays(-fromDaysAgo - 5), Remark = "年度議價合約（系統上線前建立）"
            };
            c.No = $"CT-{c.CreatedAt:yyyy}-{c.Id:000}";
            c.Logs.Add(new AuditLog { At = c.CreatedAt, User = "李志明（採購主管）", Action = $"建立年度合約，有效期 {c.ValidFrom:yyyy/MM/dd} ~ {c.ValidTo:yyyy/MM/dd}" });
            Contracts.Add(c);
            return c;
        }
        var ctScrew = Legacy(5, 6, 400, 350, 15, 300, 10, 7);          // 即將到期 → 可示範續約詢價
        var ctWasher = Legacy(5, 7, 178, 340, 25, 300, 10, 7);         // 即將到期
        var ctBox = Legacy(3, 8, 31, 385, -20, 20000, 1000, 10);       // 已到期
        var ctBag = Legacy(7, 9, 4.1m, 60, 305, 200000, 5000, 5, (5000, 4.3m), (20000, 4.1m), (50000, 3.95m)); // 階梯合約價
        var ctCase = Legacy(1, 1, 470, 100, 265, 3000, 500, 28);       // 生效中（r7 簽核通過後可由新合約取代）
        var ctCover = Legacy(8, 10, 58, 200, 165, 10000, 1000, 30);    // 之後被終止

        _clock = At(200); ReleaseOrder(ctScrew.Id, 120, today.AddDays(-190), "新竹廠");
        _clock = At(40); ReleaseOrder(ctScrew.Id, 140, today.AddDays(-33), "新竹廠");
        _clock = At(100); ReleaseOrder(ctWasher.Id, 150, today.AddDays(-93), "新竹廠");
        _clock = At(300); ReleaseOrder(ctBox.Id, 6000, today.AddDays(-290), "台中倉");
        _clock = At(120); ReleaseOrder(ctBox.Id, 8000, today.AddDays(-110), "台中倉");
        _clock = At(30); ReleaseOrder(ctBag.Id, 80000, today.AddDays(-23), "新竹廠");
        _clock = At(50); ReleaseOrder(ctCase.Id, 1000, today.AddDays(-20), "新竹廠");
        _clock = At(150); ReleaseOrder(ctCover.Id, 2000, today.AddDays(-120), "新竹廠");
        _clock = At(45); TerminateContract(ctCover.Id, "供應商品質異常（進料不良率 3.2%），已移出合格供應商名單");

        // 由詢價單 r1 決標結果建立的合約
        _clock = At(31);
        CreateContractsFromRfq(r1.Id, today.AddDays(-31), today.AddDays(-31).AddYears(1).AddDays(-1), new() { [1] = 1200, [2] = 1200 });
        _clock = At(10); ReleaseOrder(Contracts.First(c => c.SourceRfqId == r1.Id && c.MaterialId == 4).Id, 200, today.AddDays(5), "新竹廠");
        foreach (var po in PurchaseOrders.Where(p => p.ContractId != null && p.CreatedAt < today.AddDays(-7))) { po.Status = "供應商已確認"; po.ConfirmedAt = po.CreatedAt.AddDays(1); }

        _clock = null;
    }
}

public class BusinessException(string message) : Exception(message);
