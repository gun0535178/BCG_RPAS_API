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
        /// ประมวลผล ErpJob รายการเดียว โดยปฏิบัติตาม CHECK CONSTRAINTS ใน BCG_RPAS.sql อย่างเคร่งครัด:
        /// - CK_ErpJobs_Lease: Processing ต้องมี LockedUntilDate & LockToken, สถานะอื่นต้องเป็น NULL
        /// - CK_ErpJobs_Schedule: Pending/RetryScheduled ต้องมี NextAttemptDate, สถานะอื่นต้องเป็น NULL
        /// - CK_ErpJobs_Completion: Succeeded/Failed ต้องมี CompletedDate, สถานะอื่นต้องเป็น NULL
        /// - CK_ErpJobs_RetryBudget: Pending/RetryScheduled ต้องมี AttemptCount < MaxAttempts
        /// - CK_ErpAttempts_Outcome: Outcome IN ('InProgress','Succeeded','TransientFailure','PermanentFailure','Unknown')
        /// - UQ_ErpAttempts_JobAttempt: UNIQUE (JobId, AttemptNumber)
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

            // หา ActorId สำหรับบันทึก Log/CreateBy (ใช้ผู้กดสั่ง หรือ System User Id=1)
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

            var startTime = DateTime.Now;

            // หากงานเคยล้มเหลวครบโควตา (AttemptCount >= MaxAttempts) แล้วผู้รับผิดชอบกดปุ่ม "ส่งข้อมูลซ้ำ" (forceImmediate)
            // ให้เพิ่มโควตา MaxAttempts ครั้งละ 5 (ไม่เกิน 100 ตาม CK_ErpJobs_Attempts) เพื่อไม่ให้ AttemptNumber ซ้ำกับ UQ_ErpAttempts_JobAttempt
            if (forceImmediate && job.AttemptCount >= job.MaxAttempts)
            {
                job.MaxAttempts = Math.Min(100, job.MaxAttempts + 5);
            }

            // หา AttemptNumber ถัดไปที่ไม่ซ้ำกับประวัติเดิมในตาราง ErpAttempts
            var maxExistingAttemptQuery =
                from _data in bcg.ErpAttempts.AsNoTracking()
                where _data.JobId == job.Id
                select (int?)_data.AttemptNumber;

            var maxExistingAttempt = await maxExistingAttemptQuery.MaxAsync(cancellationToken) ?? 0;
            var currentAttemptNumber = Math.Max(job.AttemptCount + 1, maxExistingAttempt + 1);

            if (currentAttemptNumber > job.MaxAttempts)
            {
                job.MaxAttempts = Math.Min(100, currentAttemptNumber);
            }

            var correlationId = $"FLOW-REQ-{job.RequestId:D4}";
            var traceId = $"{correlationId}|ATTEMPT-{currentAttemptNumber}";

            // บันทึก Log การเริ่มส่งข้อมูล
            bcg.Logs.Add(new Log
            {
                RequestId = job.RequestId,
                ActorId = effectiveActorId,
                Action = DataFactory.LogAction.LogActionErpAttempt,
                FromStatusId = DataFactory.StatusApproved,
                ToStatusId = DataFactory.StatusApproved,
                RequestRevisionId = job.RequestRevisionId,
                ActionDate = startTime,
                Details = $"เริ่มส่งข้อมูลไปยัง ERP ครั้งที่ {currentAttemptNumber}/{job.MaxAttempts}",
                TraceId = traceId,
                Active = 1,
                CreateBy = effectiveActorId,
                CreateDate = startTime
            });

            // จำลองการส่งข้อมูลไปยังระบบ ERP (FinishedDate >= StartedDate เสมอตาม CK_ErpAttempts_Finished)
            var finishTime = DateTime.Now;
            if (finishTime < startTime)
            {
                finishTime = startTime;
            }

            bool isSuccess = !_simulateFailureMode;

            // เคลียร์ Lease fields เสมอเมื่อจบการทำงาน (ตาม CK_ErpJobs_Lease: Status <> 'Processing' ต้องมี LockedUntilDate IS NULL AND LockToken IS NULL)
            job.LockedUntilDate = null;
            job.LockToken = null;
            job.AttemptCount = currentAttemptNumber;
            job.ModifyBy = effectiveActorId;
            job.ModifyDate = finishTime;
            job.ConcurrencyVersion += 1;

            rentalRequest.ModifyBy = effectiveActorId;
            rentalRequest.ModifyDate = finishTime;
            rentalRequest.ConcurrencyVersion += 1;

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
                    StartedDate = startTime,
                    FinishedDate = finishTime,
                    Outcome = "Succeeded", // ตรงตาม CK_ErpAttempts_Outcome
                    HttpStatus = 200,      // ตรงตาม CK_ErpAttempts_Success (200..299)
                    Error = null,
                    ResponseSummary = responseJson,
                    TraceId = traceId,
                    Active = 1,
                    CreateBy = effectiveActorId,
                    CreateDate = finishTime
                });

                job.Status = DataFactory.SyncStatus.SyncStatusSucceeded;
                job.ExternalReference = erpRef; // ตรงตาม CK_ErpJobs_Reference
                job.LastError = null;
                job.CompletedDate = finishTime; // ตรงตาม CK_ErpJobs_Completion
                job.NextAttemptDate = null;     // ตรงตาม CK_ErpJobs_Schedule

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

                _logger.LogInformation(
                    "[CorrelationId: {CorrelationId}] [STEP: ERP_SYNC_SUCCEEDED (ส่งข้อมูลไป ERP สำเร็จ ครั้งที่ {Attempt}/{Max})]\n  Actor   : SYSTEM_WORKER | JobId: #{JobId}\n  OUTBOUND_TO_ERP  [{StartTime:dd/MM/yyyy HH:mm:ss.fff}] : {Outbound}\n  INBOUND_FROM_ERP [{FinishTime:dd/MM/yyyy HH:mm:ss.fff}] : HTTP 200 OK -> {Inbound}",
                    correlationId, currentAttemptNumber, job.MaxAttempts, job.Id, startTime, job.PayloadSnapshot, finishTime, responseJson);
            }
            else
            {
                var errorMessage = "เซิร์ฟเวอร์ ERP ไม่ตอบสนอง หรือปฏิเสธการเชื่อมต่อชั่วคราว (HTTP 503)";
                var responseJson = JsonSerializer.Serialize(new
                {
                    status = "ERROR",
                    httpStatus = 503,
                    error = errorMessage,
                    failedAt = finishTime
                });

                bool hasMoreRetries = currentAttemptNumber < job.MaxAttempts;
                var stepTitle = hasMoreRetries
                    ? $"ERP_SYNC_RETRY_SCHEDULED (ส่งไป ERP ไม่สำเร็จ ครั้งที่ {currentAttemptNumber}/{job.MaxAttempts} - รอส่งซ้ำ)"
                    : $"ERP_SYNC_FAILED_FINAL (ส่งไป ERP ล้มเหลวครบ {job.MaxAttempts} ครั้ง - รอผู้รับผิดชอบตรวจสอบ)";

                _logger.LogWarning(
                    "[CorrelationId: {CorrelationId}] [STEP: {StepTitle}]\n  Actor   : SYSTEM_WORKER | JobId: #{JobId}\n  OUTBOUND_TO_ERP  [{StartTime:dd/MM/yyyy HH:mm:ss.fff}] : {Outbound}\n  INBOUND_FROM_ERP [{FinishTime:dd/MM/yyyy HH:mm:ss.fff}] : HTTP 503 ERROR -> {Inbound}",
                    correlationId, stepTitle, job.Id, startTime, job.PayloadSnapshot, finishTime, responseJson);
                // ต้องใช้ 'TransientFailure' หรือ 'PermanentFailure' ตาม CK_ErpAttempts_Outcome ใน BCG_RPAS.sql!
                var attemptOutcome = hasMoreRetries ? "TransientFailure" : "PermanentFailure";

                bcg.ErpAttempts.Add(new ErpAttempt
                {
                    JobId = job.Id,
                    AttemptNumber = currentAttemptNumber,
                    StartedDate = startTime,
                    FinishedDate = finishTime,
                    Outcome = attemptOutcome,
                    HttpStatus = 503,
                    Error = errorMessage, // ตรงตาม CK_ErpAttempts_Error (ต้องมี Error เมื่อเป็น TransientFailure/PermanentFailure)
                    ResponseSummary = responseJson,
                    TraceId = traceId,
                    Active = 1,
                    CreateBy = effectiveActorId,
                    CreateDate = finishTime
                });

                job.LastError = errorMessage;

                if (hasMoreRetries)
                {
                    var nextRetry = finishTime.AddSeconds(20);
                    job.Status = DataFactory.SyncStatus.SyncStatusRetryScheduled;
                    job.NextAttemptDate = nextRetry; // ตรงตาม CK_ErpJobs_Schedule
                    job.CompletedDate = null;        // ตรงตาม CK_ErpJobs_Completion

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
                    job.Status = DataFactory.SyncStatus.SyncStatusFailed;
                    job.NextAttemptDate = null;     // ตรงตาม CK_ErpJobs_Schedule
                    job.CompletedDate = finishTime; // ตรงตาม CK_ErpJobs_Completion

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
