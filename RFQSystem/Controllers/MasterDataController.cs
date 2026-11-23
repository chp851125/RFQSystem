using Microsoft.AspNetCore.Mvc;
using RFQSystem.Services;

namespace RFQSystem.Controllers;

public class SuppliersController(MockDb db) : Controller
{
    public IActionResult Index() => View(db);

    public IActionResult Details(int id)
    {
        if (db.Suppliers.All(s => s.Id != id)) return NotFound();
        ViewBag.Id = id;
        return View(db);
    }
}

public class MaterialsController(MockDb db) : Controller
{
    public IActionResult Index() => View(db);

    public IActionResult Details(int id)
    {
        if (db.Materials.All(m => m.Id != id)) return NotFound();
        ViewBag.Id = id;
        return View(db);
    }
}

public class PurchaseOrdersController(MockDb db) : Controller
{
    public IActionResult Index() => View(db);

    public IActionResult Details(int id)
    {
        var po = db.PurchaseOrders.FirstOrDefault(p => p.Id == id);
        if (po == null) return NotFound();
        ViewBag.Db = db;
        return View(po);
    }
}
