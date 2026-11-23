using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using RFQSystem.Models;
using RFQSystem.Services;

namespace RFQSystem.Controllers;

/// <summary>
/// 供應商入口網站：供應商登入後自行查看詢價、報價、回覆議價、確認採購單。
/// Demo 以 Cookie 記住「目前登入的供應商」，實務上應使用帳號密碼 / SSO 登入。
/// 原則：供應商只能看到自己的資料，看不到目標價與其他廠商的報價。
/// </summary>
public class PortalController(MockDb db) : Controller
{
    private const string CookieName = "portal_sid";
    private Supplier? _me;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (int.TryParse(Request.Cookies[CookieName], out var sid))
            _me = db.Suppliers.FirstOrDefault(s => s.Id == sid && s.IsQualified);
        var action = context.ActionDescriptor.RouteValues["action"];
        if (_me == null && action is not (nameof(Login) or nameof(Logout)))
        {
            context.Result = RedirectToAction(nameof(Login));
            return;
        }
        ViewBag.Me = _me;
        base.OnActionExecuting(context);
    }

    [HttpGet]
    public IActionResult Login() => View(db);

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Login(int supplierId)
    {
        Response.Cookies.Append(CookieName, supplierId.ToString(), new CookieOptions { HttpOnly = true });
        return RedirectToAction(nameof(Index));
    }

    public IActionResult Logout()
    {
        Response.Cookies.Delete(CookieName);
        return RedirectToAction(nameof(Login));
    }

    /// <summary>我的詢價邀請</summary>
    public IActionResult Index()
    {
        var list = MyRfqs().OrderByDescending(r => db.PortalStatus(r, _me!.Id).NeedAction).ThenByDescending(r => r.Id).ToList();
        return View(list);
    }

    public IActionResult Rfq(int id)
    {
        var rfq = MyRfqs().FirstOrDefault(r => r.Id == id);
        if (rfq == null) return NotFound();
        ViewBag.Db = db;
        return View(rfq);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Quote(int id, Dictionary<int, decimal> prices, Dictionary<int, int> leads, Dictionary<int, decimal> moqs,
        string? paymentTerms, DateTime? validUntil, string? remark,
        List<int>? tierLine, List<decimal?>? tierQty, List<decimal?>? tierPrice) =>
        Run(id, () =>
        {
            db.SubmitQuote(id, new Quotation
            {
                SupplierId = _me!.Id,
                PaymentTerms = paymentTerms ?? "",
                ValidUntil = validUntil ?? default,
                Remark = remark ?? "",
                Lines = QuoteForm.BuildLines(prices, leads, moqs, tierLine, tierQty, tierPrice)
            }, fromPortal: true);
            return "報價已送出，感謝您的報價";
        });

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Decline(int id, string? reason) =>
        Run(id, () => { db.DeclineQuote(id, _me!.Id, reason ?? ""); return "已回覆婉拒報價"; });

    /// <summary>我的採購單</summary>
    public IActionResult Orders()
    {
        ViewBag.Db = db;
        return View(db.PurchaseOrders.Where(p => p.SupplierId == _me!.Id).OrderByDescending(p => p.Id).ToList());
    }

    /// <summary>我的合約：供應商查看與本公司簽訂的合約價與使用狀況</summary>
    public IActionResult Contracts()
    {
        ViewBag.Db = db;
        return View(db.Contracts.Where(c => c.SupplierId == _me!.Id).OrderBy(c => c.IsActive ? 0 : 1).ThenBy(c => c.ValidTo).ToList());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult ConfirmPo(int poId)
    {
        try { db.ConfirmPo(poId, _me!.Id); TempData["Msg"] = "已確認接單"; }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Orders));
    }

    private IEnumerable<Rfq> MyRfqs() =>
        db.Rfqs.Where(r => r.Status != RfqStatus.Draft && r.InvitedSupplierIds.Contains(_me!.Id));

    private IActionResult Run(int id, Func<string> action)
    {
        if (!MyRfqs().Any(r => r.Id == id)) return NotFound();
        try { TempData["Msg"] = action(); }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Rfq), new { id });
    }
}
