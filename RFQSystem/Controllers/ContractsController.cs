using Microsoft.AspNetCore.Mvc;
using RFQSystem.Services;

namespace RFQSystem.Controllers;

/// <summary>合約價管理：查詢合約、依合約下單、終止、續約詢價</summary>
public class ContractsController(MockDb db) : Controller
{
    public IActionResult Index(string? status, int? materialId)
    {
        var list = db.Contracts.AsEnumerable();
        if (!string.IsNullOrEmpty(status)) list = list.Where(c => c.Status == status);
        if (materialId.HasValue) list = list.Where(c => c.MaterialId == materialId);
        ViewBag.Status = status;
        ViewBag.MaterialId = materialId;
        ViewBag.Db = db;
        return View(list.OrderBy(c => c.Status switch { "即將到期" => 0, "生效中" => 1, "未生效" => 2, _ => 3 }).ThenBy(c => c.ValidTo).ToList());
    }

    public IActionResult Details(int id)
    {
        var c = db.FindContract(id);
        if (c == null) return NotFound();
        ViewBag.Db = db;
        return View(c);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Release(int id, decimal qty, DateTime deliveryDate, string? deliveryPlace)
    {
        try
        {
            var (po, warning) = db.ReleaseOrder(id, qty, deliveryDate, deliveryPlace ?? "");
            TempData["Msg"] = $"已依合約開立採購單 {po.No}，金額 {po.Total:N0}" + (warning == null ? "" : "。" + warning);
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Terminate(int id, string? reason)
    {
        try { db.TerminateContract(id, reason ?? ""); TempData["Msg"] = "合約已終止"; }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Renew(int id)
    {
        try
        {
            var rfq = db.CreateRenewalRfq(id);
            TempData["Msg"] = $"已建立續約詢價單 {rfq.No}（草稿），已帶入料件、數量、目標價與建議邀請名單，確認後即可發出";
            return RedirectToAction("Details", "Rfq", new { id = rfq.Id, tab = "suppliers" });
        }
        catch (BusinessException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }
}
