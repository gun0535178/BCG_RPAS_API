using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers
{
    public class ApprovalAssignmentsController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
