using System.Text.Json;

namespace BCG_RPAS_API.Services
{
    public class ErpSyncService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<ErpSyncService> _logger;

        // โหมดจำลอง ERP: false = ส่งสำเร็จปกติ (200 OK), true = จำลองระบบ ERP ขัดข้อง (503 Fail)
        private static bool _simulateFailureMode = false;

        public ErpSyncService(
            IServiceScopeFactory scopeFactory,
            ILogger<ErpSyncService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public bool GetSimulateFailureMode() => _simulateFailureMode;

        public void SetSimulateFailureMode(bool simulateFailure)
        {
            _simulateFailureMode = simulateFailure;
        }

        /// <summary>
        /// ประมวลผลคิวงาน ErpJobs ที่อยู่ในสถานะ Pending หรือ RetryScheduled ที่ถึงเวลาส่งแล้ว
        /// </summary>
        public async Task<int> ProcessDueJobsAsync(int? actorUserId, CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var bcg = scope.ServiceProvider.GetRequiredService<BCG_RPASContext>();

            var now = DateTime.Now;

            var dueJobsQuery =
                from _data in bcg.ErpJobs.AsNoTracking()
                where _data.Active == 1
                    && (_data.Status == DataFactory.SyncStatus.SyncStatusPending ||
                        _data.Status == DataFactory.SyncStatus.SyncStatusRetryScheduled)
                    && (_data.NextAttemptDate == null || _data.NextAttemptDate <= now)
                orderby _data.Id
                select _data.Id;

            var dueJobIds = await dueJobsQuery.Take(20).ToListAsync(cancellationToken);

            int processedCount = 0;
            foreach (var jobId in dueJobIds)
            {
                var ok = await ProcessSingleJobAsync(jobId, actorUserId, forceImmediate: false, cancellationToken);
                if (ok) processedCount++;
            }

            return processedCount;
        }

        /// <summary>
        /// ประมวลผล ErpJob รายการเดียว (รองรับทั้งจาก Background Worker และการกดส่งซ้ำด้วยตนเอง)
        /// </summary>
        public async Task<bool> ProcessSingleJobAsync(
            int jobId,
            int? actorUserId,
            bool forceImmediate,
            CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var bcg = scope.ServiceProvider.GetRequiredService<BCG_RPASContext>();

            var jobQuery =
                from _data in bcg.ErpJobs
                where _data.Active == 1
                    && _data.Id == jobId
                select _data;

            var job = await jobQuery.SingleOrDefaultAsync(cancellationToken);
            if (job == null)
            {
                return false;
            }

            // หากส่งสำเร็จไปแล้ว ไม่ต้องส่งซ้ำอีก
            if (job.Status == DataFactory.SyncStatus.SyncStatusSucceeded)
            {
                return false;
            }

            var reqQuery =
                from _data in bcg.RentalRequests
                where _data.Active == 1
                    && _data.Id == job.RequestId
                select _data;

            var rentalRequest = await reqQuery.SingleOrDefaultAsync(cancellationToken);
            if (rentalRequest == null)
            {
                return false;
            }

            // หา ActorId สำหรับบันทึก Log/CreateBy (ใช้ผู้กดสั่ง หรือ System User หรือผู้สร้างงานเดิม)
            int effectiveActorId = actorUserId ?? job.ModifyBy ?? job.CreateBy;
            if (!actorUserId.HasValue)
            {
                var sysUserQuery =
                    from _data in bcg.Users.AsNoTracking()
                    where _data.Active == 1
                        && _data.RoleId == DataFactory.RoleSystem
                    orderby _data.Id
                    select (int?)_data.Id;

                var sysUserId = await sysUserQuery.FirstOrDefaultAsync(cancellationToken);
                if (sysUserId.HasValue)
                {
                    effectiveActorId = sysUserId.Value;
                }
            }

            var now = DateTime.Now;

            // กรณีงานเป็น Failed (ครบ 5 ครั้งแล้ว) และผู้รับผิดชอบกดปุ่ม "ส่งข้อมูลซ้ำ" (forceImmediate) -> รีเซ็ตรอบการส่งใหม่
            if (forceImmediate && job.AttemptCount >= job.MaxAttempts)
            {
                job.AttemptCount = 0;
            }

            var currentAttemptNumber = job.AttemptCount + 1;
            var traceId = Guid.NewGuid().ToString("N");

            // บันทึกการเริ่มต้นส่งข้อมูล
            job.Status = DataFactory.SyncStatus.SyncStatusProcessing;
            job.AttemptCount = currentAttemptNumber;
            job.ModifyBy = effectiveActorId;
            job.ModifyDate = now;
            job.ConcurrencyVersion += 1;

            rentalRequest.SyncStatus = DataFactory.SyncStatus.SyncStatusProcessing;
            rentalRequest.ModifyBy = effectiveActorId;
            rentalRequest.ModifyDate = now;
            rentalRequest.ConcurrencyVersion += 1;

            bcg.Logs.Add(new Log
            {
                RequestId = job.RequestId,
                ActorId = effectiveActorId,
                Action = DataFactory.LogAction.LogActionErpAttempt,
                FromStatusId = DataFactory.StatusApproved,
                ToStatusId = DataFactory.StatusApproved,
                RequestRevisionId = job.RequestRevisionId,
                ActionDate = now,
                Details = $"เริ่มส่งข้อมูลไปยัง ERP ครั้งที่ {currentAttemptNumber}/{job.MaxAttempts}",
                TraceId = traceId,
                Active = 1,
                CreateBy = effectiveActorId,
                CreateDate = now
            });

            // จำลองการส่งข้อมูลไปยังระบบ ERP (Mock ERP Execution)
            var finishTime = DateTime.Now;
            bool isSuccess = !_simulateFailureMode;

            if (isSuccess)
            {
                var erpRef = $"ERP-{finishTime.Year}-{job.RequestId:D4}-{currentAttemptNumber:D2}";
                var responseJson = JsonSerializer.Serialize(new
                {
                    status = "SUCCESS",
                    erpReference = erpRef,
                    processedAt = finishTime,
                    idempotencyKey = job.IdempotencyKey
                });

                bcg.ErpAttempts.Add(new ErpAttempt
                {
                    JobId = job.Id,
                    AttemptNumber = currentAttemptNumber,
                    StartedDate = now,
                    FinishedDate = finishTime,
                    Outcome = "Succeeded",
                    HttpStatus = 200,
                    Error = null,
                    ResponseSummary = responseJson,
                    TraceId = traceId,
                    Active = 1,
                    CreateBy = effectiveActorId,
                    CreateDate = finishTime
                });

                job.Status = DataFactory.SyncStatus.SyncStatusSucceeded;
                job.ExternalReference = erpRef;
                job.LastError = null;
                job.CompletedDate = finishTime;
                job.NextAttemptDate = null;

                rentalRequest.SyncStatus = DataFactory.SyncStatus.SyncStatusSucceeded;

                bcg.Logs.Add(new Log
                {
                    RequestId = job.RequestId,
                    ActorId = effectiveActorId,
                    Action = DataFactory.LogAction.LogActionErpSucceeded,
                    FromStatusId = DataFactory.StatusApproved,
                    ToStatusId = DataFactory.StatusApproved,
                    RequestRevisionId = job.RequestRevisionId,
                    ActionDate = finishTime,
                    Details = $"ส่งข้อมูลไปยัง ERP สำเร็จ (เลขอ้างอิง: {erpRef})",
                    TraceId = traceId,
                    Active = 1,
                    CreateBy = effectiveActorId,
                    CreateDate = finishTime
                });
            }
            else
            {
                var errorMessage = "เซิร์ฟเวอร์ ERP ไม่ตอบสนอง หรือปฏิเสธการเชื่อมต่อชั่วคราว (HTTP 503 Service Unavailable)";
                var responseJson = JsonSerializer.Serialize(new
                {
                    status = "ERROR",
                    httpStatus = 503,
                    error = errorMessage,
                    failedAt = finishTime
                });

                bcg.ErpAttempts.Add(new ErpAttempt
                {
                    JobId = job.Id,
                    AttemptNumber = currentAttemptNumber,
                    StartedDate = now,
                    FinishedDate = finishTime,
                    Outcome = "Failed",
                    HttpStatus = 503,
                    Error = errorMessage,
                    ResponseSummary = responseJson,
                    TraceId = traceId,
                    Active = 1,
                    CreateBy = effectiveActorId,
                    CreateDate = finishTime
                });

                job.LastError = errorMessage;

                if (currentAttemptNumber < job.MaxAttempts)
                {
                    // ยังไม่ครบจำนวนครั้งสูงสุด (5 ครั้ง) -> วางแผนส่งซ้ำตามเงื่อนไข
                    var nextRetry = finishTime.AddSeconds(20);
                    job.Status = DataFactory.SyncStatus.SyncStatusRetryScheduled;
                    job.NextAttemptDate = nextRetry;

                    rentalRequest.SyncStatus = DataFactory.SyncStatus.SyncStatusRetryScheduled;

                    bcg.Logs.Add(new Log
                    {
                        RequestId = job.RequestId,
                        ActorId = effectiveActorId,
                        Action = DataFactory.LogAction.LogActionErpRetry,
                        FromStatusId = DataFactory.StatusApproved,
                        ToStatusId = DataFactory.StatusApproved,
                        RequestRevisionId = job.RequestRevisionId,
                        ActionDate = finishTime,
                        Reason = errorMessage,
                        Details = $"ส่งข้อมูลไปยัง ERP ครั้งที่ {currentAttemptNumber}/{job.MaxAttempts} ไม่สำเร็จ กำหนดส่งซ้ำถัดไปเวลา {nextRetry:dd/MM/yyyy HH:mm:ss}",
                        TraceId = traceId,
                        Active = 1,
                        CreateBy = effectiveActorId,
                        CreateDate = finishTime
                    });
                }
                else
                {
                    // ครบจำนวนครั้งสูงสุด (5 ครั้ง) แล้วยังไม่สำเร็จ -> บันทึกว่าล้มเหลวและรอผู้รับผิดชอบมาตรวจสอบ
                    job.Status = DataFactory.SyncStatus.SyncStatusFailed;
                    job.NextAttemptDate = null;
                    job.CompletedDate = finishTime;

                    rentalRequest.SyncStatus = DataFactory.SyncStatus.SyncStatusFailed;

                    bcg.Logs.Add(new Log
                    {
                        RequestId = job.RequestId,
                        ActorId = effectiveActorId,
                        Action = DataFactory.LogAction.LogActionErpFailed,
                        FromStatusId = DataFactory.StatusApproved,
                        ToStatusId = DataFactory.StatusApproved,
                        RequestRevisionId = job.RequestRevisionId,
                        ActionDate = finishTime,
                        Reason = errorMessage,
                        Details = $"ส่งข้อมูลไปยัง ERP ล้มเหลวครบกำหนด {job.MaxAttempts} ครั้ง รอผู้รับผิดชอบตรวจสอบ",
                        TraceId = traceId,
                        Active = 1,
                        CreateBy = effectiveActorId,
                        CreateDate = finishTime
                    });
                }
            }

            await bcg.SaveChangesAsync(cancellationToken);
            return true;
        }
    }
}
