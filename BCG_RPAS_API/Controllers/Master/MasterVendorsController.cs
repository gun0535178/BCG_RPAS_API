using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.Master
{
    public class MasterVendorsController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
