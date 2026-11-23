using Microsoft.AspNetCore.Mvc;
using RFQSystem.Models;
using RFQSystem.Services;
using System.Diagnostics;

namespace RFQSystem.Controllers
{
    public class HomeController(MockDb db) : Controller
    {
        /// <summary>儀表板</summary>
        public IActionResult Index() => View(db);

        /// <summary>功能說明：企業詢比議價常見功能與名詞</summary>
        public IActionResult Guide() => View();

        /// <summary>重置示範資料</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public IActionResult Reset()
        {
            db.Reset();
            TempData["Msg"] = "示範資料已重置為初始狀態";
            return RedirectToAction(nameof(Index));
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
