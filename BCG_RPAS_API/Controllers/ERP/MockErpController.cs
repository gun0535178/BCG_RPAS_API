using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.ERP
{
    [ApiController]
    [Authorize]
    [Tags("ERP")]
    [Route("api/erp/mock")]
    public class MockErpController : ControllerBase
    {
        private readonly ErpSyncService _erpSyncService;

        public MockErpController(ErpSyncService erpSyncService)
        {
            _erpSyncService = erpSyncService;
        }

        [HttpGet("status")]
        public IActionResult GetMockStatus()
        {
            var isFailing = _erpSyncService.GetSimulateFailureMode();
            return Ok(new
            {
                simulateFailure = isFailing,
                modeText = isFailing ? "โหมดจำลอง ERP ขัดข้อง (ส่งไม่สำเร็จ)" : "โหมดปกติ (ส่งสำเร็จ)"
            });
        }

        [HttpPost("payments")]
        public IActionResult ReceivePayment([FromBody] JsonElement payload)
        {
            if (_erpSyncService.GetSimulateFailureMode())
            {
                return StatusCode(503, new
                {
                    status = "ERROR",
                    detail = "เซิร์ฟเวอร์ ERP ไม่ตอบสนองชั่วคราว"
                });
            }

            var refNo = $"ERP-{DateTime.Now.Year}-{Random.Shared.Next(1000, 9999)}";
            return Ok(new
            {
                status = "SUCCESS",
                externalReference = refNo,
                processedDate = DateTime.Now
            });
        }
    }
}
