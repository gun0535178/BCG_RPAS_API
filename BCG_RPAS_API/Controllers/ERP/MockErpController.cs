using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.ERP
{
    public class MockErpController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
