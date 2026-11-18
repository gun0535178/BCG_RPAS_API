using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers.ERP
{
    public class SetErpMockModeModel
    {
        public bool SimulateFailure { get; set; }
    }

    [ApiController]
    [Authorize(Roles = "MANAGER,EXECUTIVE,ADMINISTRATOR")]
    [Tags("ERP")]
    [Route("api/erp/monitoring")]
    public class ErpMonitoringController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;
        private readonly ErpSyncService _erpSyncService;

        public ErpMonitoringController(
            BCG_RPASContext bcg,
            GetCurrentUser getCurrentUser,
            ErpSyncService erpSyncService)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
            _erpSyncService = erpSyncService;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
        {
            var jobsQuery =
                from _data in _bcg.ErpJobs.AsNoTracking()
                where _data.Active == 1
                orderby _data.Id descending
                select new
                {
                    _data.Id,
                    _data.RequestId,
                    _data.RequestRevisionId,
                    RevisionNumber = _data.RequestRevision.RevisionNumber,
                    ContractNumber = _data.Request.MasterContract != null ? _data.Request.MasterContract.ContractNumber : null,
                    VendorName = _data.Request.Vendor != null ? _data.Request.Vendor.Name : null,
                    RequesterName = _data.Request.Requester.DisplayName,
                    TotalAmount = _data.Request.TotalAmount,
                    _data.IdempotencyKey,
                    _data.Status,
                    _data.AttemptCount,
                    _data.MaxAttempts,
                    _data.NextAttemptDate,
                    _data.LastError,
                    _data.ExternalReference,
                    _data.CompletedDate,
                    _data.CreateDate,
                    _data.ModifyDate
                };

            var jobs = await jobsQuery.ToListAsync(cancellationToken);

            return Ok(new
            {
                simulateFailure = _erpSyncService.GetSimulateFailureMode(),
                jobs
            });
        }

        [HttpGet("{jobId:int}/attempts")]
        public async Task<IActionResult> GetAttempts(int jobId, CancellationToken cancellationToken)
        {
            var attemptsQuery =
                from _data in _bcg.ErpAttempts.AsNoTracking()
                where _data.Active == 1
                    && _data.JobId == jobId
                orderby _data.AttemptNumber descending
                select new
                {
                    _data.Id,
                    _data.JobId,
                    _data.AttemptNumber,
                    _data.StartedDate,
                    _data.FinishedDate,
                    _data.Outcome,
                    _data.HttpStatus,
                    _data.Error,
                    _data.ResponseSummary,
                    _data.TraceId
                };

            var attempts = await attemptsQuery.ToListAsync(cancellationToken);
            return Ok(attempts);
        }

        [HttpPost("{jobId:int}/retry")]
        public async Task<IActionResult> RetryJob(int jobId, CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var ok = await _erpSyncService.ProcessSingleJobAsync(jobId, userId.Value, forceImmediate: true, cancellationToken);
            if (!ok)
            {
                return BadRequest(new { detail = "ไม่พบรายการคิว ERP หรือรายการนี้ซิงค์สำเร็จไปแล้ว" });
            }

            return Ok(new { message = "สั่งประมวลผลส่งข้อมูลไปยัง ERP เรียบร้อยแล้ว" });
        }

        [HttpPost("process-due")]
        public async Task<IActionResult> ProcessDueJobs(CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var count = await _erpSyncService.ProcessDueJobsAsync(userId.Value, cancellationToken);
            return Ok(new
            {
                processedCount = count,
                message = count > 0
                    ? $"ประมวลผลคิว ERP จำนวน {count} รายการเรียบร้อยแล้ว"
                    : "ไม่มีรายการคิว ERP ที่รอส่งในขณะนี้"
            });
        }

        [HttpPost("mode")]
        public IActionResult SetMockMode([FromBody] SetErpMockModeModel request)
        {
            _erpSyncService.SetSimulateFailureMode(request.SimulateFailure);
            return Ok(new
            {
                simulateFailure = request.SimulateFailure,
                message = request.SimulateFailure
                    ? "เปลี่ยนเป็น 'โหมดจำลอง ERP ขัดข้อง' (การส่งครั้งถัดไปจะล้มเหลวเพื่อทดสอบการส่งซ้ำ)"
                    : "เปลี่ยนเป็น 'โหมดปกติ' (การส่งครั้งถัดไปจะสำเร็จและได้รับเลขอ้างอิง ERP)"
            });
        }
    }
}
