using Microsoft.AspNetCore.Mvc;
using RFQSystem.Models;
using RFQSystem.Services;

namespace RFQSystem.Controllers;

public class RfqController(MockDb db) : Controller
{
    public IActionResult Index(RfqStatus? status, string? q)
    {
        var list = db.Rfqs.AsEnumerable();
        if (status.HasValue) list = list.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(r => r.No.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(q));
        ViewBag.Status = status;
        ViewBag.Q = q;
        return View(list.OrderByDescending(r => r.Id).ToList());
    }

    public IActionResult Details(int id, string tab = "info", int pw = 60, int lw = 20, int qw = 20)
    {
        var rfq = db.FindRfq(id);
        if (rfq == null) return NotFound();
        return View(new RfqDetailVm
        {
            Rfq = rfq,
            Db = db,
            Tab = tab,
            Compare = CompareResult.Build(rfq, db, pw, lw, qw),
            Pos = db.PurchaseOrders.Where(p => p.RfqId == id).ToList()
        });
    }

    [HttpGet]
    public IActionResult Create()
    {
        SetLookups();
        return View(new RfqCreateVm { Lines = { new RfqLineInput() } });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Create(RfqCreateVm vm)
    {
        vm.Lines = vm.Lines.Where(l => l.MaterialId > 0 && l.Qty > 0).ToList();
        if (string.IsNullOrWhiteSpace(vm.Title)) ModelState.AddModelError("", "請輸入詢價主旨");
        if (vm.Lines.Count == 0) ModelState.AddModelError("", "請至少輸入一個詢價項目（料號與數量）");
        if (!ModelState.IsValid)
        {
            SetLookups();
            if (vm.Lines.Count == 0) vm.Lines.Add(new RfqLineInput());
            return View(vm);
        }
        var rfq = db.CreateRfq(new Rfq
        {
            Title = vm.Title!,
            Department = vm.Department ?? "",
            Deadline = vm.Deadline,
            DeliveryPlace = vm.DeliveryPlace ?? "",
            Remark = vm.Remark ?? "",
            SealedBid = vm.SealedBid,
            Lines = vm.Lines.Select(l => new RfqLine { MaterialId = l.MaterialId, Qty = l.Qty, TargetPrice = l.TargetPrice, RequiredDate = l.RequiredDate }).ToList()
        });
        TempData["Msg"] = $"已建立 {rfq.No}，下一步請選擇要邀請的供應商";
        return RedirectToAction(nameof(Details), new { id = rfq.Id, tab = "suppliers" });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Invite(int id, List<int> supplierIds) =>
        Run(id, "suppliers", () => { db.InviteSuppliers(id, supplierIds); return "已更新邀請名單"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Send(int id) =>
        Run(id, "quotes", () => db.Send(id) ?? "已發出詢價，等待供應商報價");

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SubmitQuote(int id, int supplierId, Dictionary<int, decimal> prices, Dictionary<int, int> leads,
        Dictionary<int, decimal> moqs, string? paymentTerms, DateTime? validUntil, string? remark,
        List<int>? tierLine, List<decimal?>? tierQty, List<decimal?>? tierPrice) =>
        Run(id, "quotes", () =>
        {
            db.SubmitQuote(id, new Quotation
            {
                SupplierId = supplierId,
                PaymentTerms = paymentTerms ?? "",
                ValidUntil = validUntil ?? default,
                Remark = remark ?? "",
                Lines = QuoteForm.BuildLines(prices, leads, moqs, tierLine, tierQty, tierPrice)
            });
            return $"已登錄 {db.Supplier(supplierId).Name} 的報價";
        });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SimulateQuotes(int id) =>
        Run(id, "quotes", () => $"已模擬 {db.SimulateQuotes(id)} 家供應商報價");

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult OpenBids(int id, string? witness) =>
        Run(id, "info", () => { db.OpenBids(id, witness ?? ""); return "開標完成，報價已解除密封，請檢視開標紀錄與比價分析"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult CloseQuoting(int id) =>
        Run(id, "compare", () => { db.CloseQuoting(id); return "已截止報價，請檢視比價分析"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Negotiate(int id, int supplierId, Dictionary<int, decimal> counter, string? message) =>
        Run(id, "negotiate", () => { db.Negotiate(id, supplierId, counter, message ?? ""); return "已送出議價，等待供應商回覆"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult SimulateReply(int id, int negotiationId) =>
        Run(id, "negotiate", () => { db.SimulateNegotiationReply(id, negotiationId); return "供應商已回覆議價並重新報價"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Award(int id, Dictionary<int, int> award, string? reason) =>
        Run(id, "award", () => { db.SubmitAward(id, award, reason ?? ""); return "已送出決標簽核"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Approve(int id, bool approve, string? comment) =>
        Run(id, "award", () => { db.Approve(id, approve, comment ?? ""); return approve ? "已核准" : "已退回，詢價單回到比價中"; });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult CreatePo(int id) =>
        Run(id, "award", () => "已產生採購單：" + string.Join("、", db.CreatePurchaseOrders(id).Select(p => p.No)));

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult CreateContracts(int id, DateTime validFrom, int months, Dictionary<int, decimal> committed) =>
        Run(id, "award", () =>
        {
            var list = db.CreateContractsFromRfq(id, validFrom, validFrom.AddMonths(months).AddDays(-1), committed);
            return "已建立合約價：" + string.Join("、", list.Select(c => c.No));
        });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Cancel(int id, string? reason) =>
        Run(id, "info", () => { db.Cancel(id, reason ?? "未填寫"); return "已取消詢價單"; });

    /// <summary>新增詢價單畫面用：料件清單，以及各料件生效中的合約（提醒採購可直接依合約下單）</summary>
    private void SetLookups()
    {
        ViewBag.Materials = db.Materials;
        ViewBag.Contracts = db.Materials.ToDictionary(m => m.Id, m => db.ActiveContracts(m.Id).Select(c => new
        {
            id = c.Id, no = c.No, supplier = db.Supplier(c.SupplierId).Name, price = c.UnitPrice, daysLeft = c.DaysLeft
        }).ToList());
    }

    private IActionResult Run(int id, string tab, Func<string> action)
    {
        try
        {
            TempData["Msg"] = action();
        }
        catch (BusinessException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Details), new { id, tab });
    }
}
