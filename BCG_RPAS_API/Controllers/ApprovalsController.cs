using System.Text.Json;
using BCG_RPAS_API.DTOs.Approvals;

namespace BCG_RPAS_API.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/approvals")]
    public class ApprovalsController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;
        private readonly ILogger<ApprovalsController> _logger;

        public ApprovalsController(
            BCG_RPASContext bcg,
            GetCurrentUser getCurrentUser,
            ILogger<ApprovalsController> logger)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
            _logger = logger;
        }

        [HttpGet("inbox")]
        public async Task<ActionResult<List<ApprovalInboxItemModel>>> GetInbox(
            CancellationToken cancellationToken)
        {
            try
            {
                var approverId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (approverId == null)
                {
                    return Unauthorized();
                }

                var query =
                    from _data in _bcg.ApprovalSteps.AsNoTracking()
                    where _data.Active == 1
                        && _data.ApproverId == approverId.Value
                        && _data.Status == DataFactory.StepStatus.StepStatusPending
                        && _data.Request.Active == 1
                        && _data.RequestRevision.Active == 1
                        && _data.RequestRevision.RevisionNumber == _data.Request.CurrentRevision
                        && ((_data.StepNumber == 1 && _data.Request.StatusId == DataFactory.StatusPendingManager) ||
                            (_data.StepNumber == 2 && _data.Request.StatusId == DataFactory.StatusPendingExecutive))
                    orderby _data.Request.SubmittedDate, _data.Request.Id
                    select new ApprovalInboxItemModel
                    {
                        RequestId = _data.RequestId,
                        StepId = _data.Id,
                        StepNumber = _data.StepNumber,
                        ApprovalRoleId = _data.ApprovalRoleId,
                        ApprovalRoleCode = _data.ApprovalRole.RoleCode,
                        ApprovalRoleName = _data.ApprovalRole.Name,
                        RequesterId = _data.Request.RequesterId,
                        RequesterName = _data.Request.Requester.DisplayName,
                        ContractId = _data.Request.ContractId,
                        ContractNumber = _data.Request.MasterContract != null ? _data.Request.MasterContract.ContractNumber : null,
                        VendorName = _data.Request.Vendor != null ? _data.Request.Vendor.Name : null,
                        RentalTypeName = _data.Request.MasterContract != null ? _data.Request.MasterContract.RentalType.Name : null,
                        CostCenterCode = _data.Request.CostCenter != null ? _data.Request.CostCenter.CostCenterCode : null,
                        CostCenterName = _data.Request.CostCenter != null ? _data.Request.CostCenter.Name : null,
                        PeriodStartDate = _data.Request.PeriodStartDate,
                        PeriodEndDate = _data.Request.PeriodEndDate,
                        Amount = _data.Request.Amount,
                        VatRate = _data.Request.VatRate,
                        VatAmount = _data.Request.VatAmount,
                        TotalAmount = _data.Request.TotalAmount,
                        Notes = _data.Request.Notes,
                        StatusId = _data.Request.StatusId,
                        StatusCode = _data.Request.Status.StatusCode,
                        StatusName = _data.Request.Status.Name,
                        CurrentRevision = _data.Request.CurrentRevision,
                        SubmittedDate = _data.Request.SubmittedDate,
                        RequestConcurrencyVersion = _data.Request.ConcurrencyVersion,
                        StepConcurrencyVersion = _data.ConcurrencyVersion
                    };

                var items = await query.ToListAsync(cancellationToken);
                return Ok(items);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะดึงข้อมูล Approval Inbox");
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถโหลดรายการงานรออนุมัติได้ในขณะนี้"
                });
            }
        }

        [HttpPost("{id:int}/approve")]
        public async Task<IActionResult> Approve(
            int id,
            [FromBody] ApproveRequestModel request,
            CancellationToken cancellationToken)
        {
            try
            {
                var approverId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (approverId == null)
                {
                    return Unauthorized();
                }

                if (id <= 0)
                {
                    return BadRequest(new { detail = "Id ต้องมากกว่า 0" });
                }

                if (request.ExpectedConcurrencyVersion is not int expectedVersion ||
                    expectedVersion <= 0 ||
                    expectedVersion == int.MaxValue)
                {
                    return BadRequest(new { detail = "ต้องส่ง ExpectedConcurrencyVersion ที่ถูกต้อง" });
                }

                var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();

                // 1. ดึงข้อมูลคำขอและ Revision ปัจจุบัน
                var requestQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == id
                    select _data;

                var rentalRequest = await requestQuery.SingleOrDefaultAsync(cancellationToken);

                if (rentalRequest == null)
                {
                    return NotFound(new { detail = "ไม่พบคำขอค่าเช่าที่ระบุ" });
                }

                if (rentalRequest.StatusId != DataFactory.StatusPendingManager &&
                    rentalRequest.StatusId != DataFactory.StatusPendingExecutive)
                {
                    return Conflict(new
                    {
                        detail = "คำขอนี้ไม่ได้อยู่ในสถานะรออนุมัติ"
                    });
                }

                if (rentalRequest.ConcurrencyVersion != expectedVersion)
                {
                    return Conflict(new
                    {
                        detail = "ข้อมูลคำขอถูกเปลี่ยนแปลงโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่"
                    });
                }

                if (rentalRequest.RequesterId == approverId.Value)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        detail = "ไม่อนุญาตให้ผู้อนุมัติทำการอนุมัติคำขอของตนเอง"
                    });
                }

                if (!rentalRequest.CurrentRevision.HasValue)
                {
                    return BadRequest(new { detail = "ไม่พบข้อมูล Revision ปัจจุบันของคำขอนี้" });
                }

                var revisionQuery =
                    from _data in _bcg.RequestRevisions.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RevisionNumber == rentalRequest.CurrentRevision.Value
                    select _data;

                var currentRevision = await revisionQuery.SingleOrDefaultAsync(cancellationToken);

                if (currentRevision == null)
                {
                    return BadRequest(new { detail = "ไม่พบข้อมูล Revision ปัจจุบันของคำขอนี้" });
                }

                // 2. ตรวจสอบขั้นตอนการอนุมัติปัจจุบัน (Step 1 สำหรับ PENDING_MANAGER, Step 2 สำหรับ PENDING_EXECUTIVE)
                var expectedStepNumber = rentalRequest.StatusId == DataFactory.StatusPendingManager ? 1 : 2;
                var expectedRoleId = expectedStepNumber == 1 ? DataFactory.RoleManager : DataFactory.RoleExecutive;

                var stepQuery =
                    from _data in _bcg.ApprovalSteps.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RequestRevisionId == currentRevision.Id
                        && _data.StepNumber == expectedStepNumber
                    select _data;

                var activeStep = await stepQuery.SingleOrDefaultAsync(cancellationToken);

                if (activeStep == null || activeStep.Status != DataFactory.StepStatus.StepStatusPending)
                {
                    return Conflict(new
                    {
                        detail = "ขั้นการอนุมัตินี้ถูกดำเนินการไปแล้ว หรือไม่อยู่ในสถานะรอดำเนินการ"
                    });
                }

                if (activeStep.ApproverId != approverId.Value || activeStep.ApprovalRoleId != expectedRoleId)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        detail = "คุณไม่ใช่ผู้อนุมัติที่ได้รับมอบหมายในขั้นตอนนี้"
                    });
                }

                var now = DateTime.Now;
                var requiresExecutive = currentRevision.Amount > DataFactory.ManagerApprovalThreshold;
                var isFinalApproval = (expectedStepNumber == 1 && !requiresExecutive) || expectedStepNumber == 2;

                await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                // อัปเดต ApprovalStep ปัจจุบันให้เป็น Approved
                var updateStepQuery =
                    from _data in _bcg.ApprovalSteps
                    where _data.Active == 1
                        && _data.Id == activeStep.Id
                        && _data.Status == DataFactory.StepStatus.StepStatusPending
                        && _data.ConcurrencyVersion == activeStep.ConcurrencyVersion
                    select _data;

                var stepAffected = await updateStepQuery
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, DataFactory.StepStatus.StepStatusApproved)
                        .SetProperty(s => s.ActionDate, (DateTime?)now)
                        .SetProperty(s => s.Reason, reason)
                        .SetProperty(s => s.ModifyBy, (int?)approverId.Value)
                        .SetProperty(s => s.ModifyDate, (DateTime?)now)
                        .SetProperty(s => s.ConcurrencyVersion, s => s.ConcurrencyVersion + 1),
                        cancellationToken);

                if (stepAffected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new { detail = "รายการอนุมัติถูกดำเนินการไปแล้ว กรุณารีเฟรชหน้าจอ" });
                }

                if (!isFinalApproval)
                {
                    // กรณี Manager อนุมัติ และยอดก่อน VAT > 50,000 บาท -> ส่งต่อให้ Executive (Step 2)
                    var nextStepQuery =
                        from _data in _bcg.ApprovalSteps
                        where _data.Active == 1
                            && _data.RequestId == id
                            && _data.RequestRevisionId == currentRevision.Id
                            && _data.StepNumber == 2
                            && _data.Status == DataFactory.StepStatus.StepStatusWaiting
                        select _data;

                    var nextStepAffected = await nextStepQuery
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(s => s.Status, DataFactory.StepStatus.StepStatusPending)
                            .SetProperty(s => s.ActionDate, (DateTime?)null)
                            .SetProperty(s => s.ModifyBy, (int?)approverId.Value)
                            .SetProperty(s => s.ModifyDate, (DateTime?)now)
                            .SetProperty(s => s.ConcurrencyVersion, s => s.ConcurrencyVersion + 1),
                            cancellationToken);

                    if (nextStepAffected == 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Conflict(new { detail = "ไม่พบขั้นตอนการอนุมัติของผู้บริหาร (Executive) ในรอบนี้" });
                    }

                    var updateReqQuery =
                        from _data in _bcg.RentalRequests
                        where _data.Active == 1
                            && _data.Id == id
                            && _data.StatusId == DataFactory.StatusPendingManager
                            && _data.ConcurrencyVersion == expectedVersion
                        select _data;

                    var reqAffected = await updateReqQuery
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(r => r.StatusId, DataFactory.StatusPendingExecutive)
                            .SetProperty(r => r.ModifyBy, (int?)approverId.Value)
                            .SetProperty(r => r.ModifyDate, (DateTime?)now)
                            .SetProperty(r => r.ConcurrencyVersion, r => r.ConcurrencyVersion + 1),
                            cancellationToken);

                    if (reqAffected == 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Conflict(new { detail = "ข้อมูลคำขอถูกแก้ไขพร้อมกัน กรุณารีเฟรชหน้าจอ" });
                    }

                    _bcg.Logs.Add(new Log
                    {
                        RequestId = id,
                        ActorId = approverId.Value,
                        Action = DataFactory.LogAction.LogActionApprove,
                        FromStatusId = DataFactory.StatusPendingManager,
                        ToStatusId = DataFactory.StatusPendingExecutive,
                        RequestRevisionId = currentRevision.Id,
                        ActionDate = now,
                        Reason = reason,
                        Details = $"ผู้จัดการอนุมัติขั้นที่ 1 สำเร็จ ส่งต่อผู้บริหาร (Revision #{currentRevision.RevisionNumber})",
                        TraceId = HttpContext.TraceIdentifier,
                        Active = 1,
                        CreateBy = approverId.Value,
                        CreateDate = now
                    });

                    await _bcg.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return Ok(new
                    {
                        requestId = id,
                        statusId = DataFactory.StatusPendingExecutive,
                        statusCode = DataFactory.StatusCode.StatusCodePendingExecutive,
                        message = "อนุมัติขั้นที่ 1 สำเร็จ ส่งต่อให้ผู้บริหาร (Executive) พิจารณา"
                    });
                }
                else
                {
                    // กรณีอนุมัติขั้นสุดท้าย -> เปลี่ยนเป็น APPROVED + สร้าง ErpJob ในธุรกรรมเดียวกันแบบ Atomic
                    var finalReqQuery =
                        from _data in _bcg.RentalRequests
                        where _data.Active == 1
                            && _data.Id == id
                            && _data.StatusId == rentalRequest.StatusId
                            && _data.ConcurrencyVersion == expectedVersion
                        select _data;

                    var reqAffected = await finalReqQuery
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(r => r.StatusId, DataFactory.StatusApproved)
                            .SetProperty(r => r.ApprovedDate, (DateTime?)now)
                            .SetProperty(r => r.SyncStatus, DataFactory.SyncStatus.SyncStatusPending)
                            .SetProperty(r => r.ModifyBy, (int?)approverId.Value)
                            .SetProperty(r => r.ModifyDate, (DateTime?)now)
                            .SetProperty(r => r.ConcurrencyVersion, r => r.ConcurrencyVersion + 1),
                            cancellationToken);

                    if (reqAffected == 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Conflict(new { detail = "ข้อมูลคำขอถูกแก้ไขพร้อมกัน กรุณารีเฟรชหน้าจอ" });
                    }

                    var idempotencyKey = Guid.NewGuid().ToString();
                    var erpPayload = new
                    {
                        RequestId = id,
                        RevisionId = currentRevision.Id,
                        RevisionNumber = currentRevision.RevisionNumber,
                        ApprovedDate = now,
                        FinalApproverId = approverId.Value,
                        IdempotencyKey = idempotencyKey,
                        ContractId = currentRevision.ContractId,
                        VendorId = currentRevision.VendorId,
                        CostCenterId = currentRevision.CostCenterId,
                        PeriodStartDate = currentRevision.PeriodStartDate,
                        PeriodEndDate = currentRevision.PeriodEndDate,
                        Amount = currentRevision.Amount,
                        VatRate = currentRevision.VatRate,
                        VatAmount = currentRevision.VatAmount,
                        TotalAmount = currentRevision.TotalAmount,
                        RevisionSnapshot = JsonSerializer.Deserialize<JsonElement>(currentRevision.SnapshotJson)
                    };

                    var erpJob = new ErpJob
                    {
                        RequestId = id,
                        RequestRevisionId = currentRevision.Id,
                        PayloadSnapshot = JsonSerializer.Serialize(erpPayload),
                        IdempotencyKey = idempotencyKey,
                        Status = DataFactory.SyncStatus.SyncStatusPending,
                        AttemptCount = 0,
                        MaxAttempts = 5,
                        NextAttemptDate = now,
                        ConcurrencyVersion = 1,
                        Active = 1,
                        CreateBy = approverId.Value,
                        CreateDate = now
                    };

                    _bcg.ErpJobs.Add(erpJob);

                    _bcg.Logs.Add(new Log
                    {
                        RequestId = id,
                        ActorId = approverId.Value,
                        Action = DataFactory.LogAction.LogActionApprove,
                        FromStatusId = rentalRequest.StatusId,
                        ToStatusId = DataFactory.StatusApproved,
                        RequestRevisionId = currentRevision.Id,
                        ActionDate = now,
                        Reason = reason,
                        Details = $"อนุมัติคำขอค่าเช่าขั้นสุดท้ายสำเร็จ (Revision #{currentRevision.RevisionNumber})",
                        TraceId = HttpContext.TraceIdentifier,
                        Active = 1,
                        CreateBy = approverId.Value,
                        CreateDate = now
                    });

                    _bcg.Logs.Add(new Log
                    {
                        RequestId = id,
                        ActorId = approverId.Value,
                        Action = DataFactory.LogAction.LogActionErpQueued,
                        FromStatusId = DataFactory.StatusApproved,
                        ToStatusId = DataFactory.StatusApproved,
                        RequestRevisionId = currentRevision.Id,
                        ActionDate = now,
                        Details = $"สร้างงานส่งข้อมูลไปยัง ERP ในคิว (IdempotencyKey: {idempotencyKey})",
                        TraceId = HttpContext.TraceIdentifier,
                        Active = 1,
                        CreateBy = approverId.Value,
                        CreateDate = now
                    });

                    await _bcg.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return Ok(new
                    {
                        requestId = id,
                        statusId = DataFactory.StatusApproved,
                        statusCode = DataFactory.StatusCode.StatusCodeApproved,
                        syncStatus = DataFactory.SyncStatus.SyncStatusPending,
                        message = "อนุมัติคำขอค่าเช่าสำเร็จ และเข้าคิวส่งข้อมูลไปยัง ERP เรียบร้อยแล้ว"
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะอนุมัติคำขอ {RequestId}", id);
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถอนุมัติคำขอได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }

        [HttpPost("{id:int}/return")]
        public async Task<IActionResult> ReturnForRevision(
            int id,
            [FromBody] DecisionWithReasonModel request,
            CancellationToken cancellationToken)
        {
            return await ProcessReturnOrRejectAsync(id, request, isReject: false, cancellationToken);
        }

        [HttpPost("{id:int}/reject")]
        public async Task<IActionResult> Reject(
            int id,
            [FromBody] DecisionWithReasonModel request,
            CancellationToken cancellationToken)
        {
            return await ProcessReturnOrRejectAsync(id, request, isReject: true, cancellationToken);
        }

        private async Task<IActionResult> ProcessReturnOrRejectAsync(
            int id,
            DecisionWithReasonModel request,
            bool isReject,
            CancellationToken cancellationToken)
        {
            try
            {
                var approverId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (approverId == null)
                {
                    return Unauthorized();
                }

                if (id <= 0)
                {
                    return BadRequest(new { detail = "Id ต้องมากกว่า 0" });
                }

                var reason = request.Reason?.Trim();
                if (string.IsNullOrWhiteSpace(reason))
                {
                    return BadRequest(new
                    {
                        detail = isReject
                            ? "กรุณาระบุเหตุผลการปฏิเสธคำขอ"
                            : "กรุณาระบุเหตุผลการส่งคืนให้แก้ไข"
                    });
                }

                if (request.ExpectedConcurrencyVersion is not int expectedVersion ||
                    expectedVersion <= 0 ||
                    expectedVersion == int.MaxValue)
                {
                    return BadRequest(new { detail = "ต้องส่ง ExpectedConcurrencyVersion ที่ถูกต้อง" });
                }

                var requestQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == id
                    select _data;

                var rentalRequest = await requestQuery.SingleOrDefaultAsync(cancellationToken);

                if (rentalRequest == null)
                {
                    return NotFound(new { detail = "ไม่พบคำขอค่าเช่าที่ระบุ" });
                }

                if (rentalRequest.StatusId != DataFactory.StatusPendingManager &&
                    rentalRequest.StatusId != DataFactory.StatusPendingExecutive)
                {
                    return Conflict(new { detail = "คำขอนี้ไม่ได้อยู่ในสถานะรออนุมัติ" });
                }

                if (rentalRequest.ConcurrencyVersion != expectedVersion)
                {
                    return Conflict(new { detail = "ข้อมูลคำขอถูกเปลี่ยนแปลงโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                if (rentalRequest.RequesterId == approverId.Value)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        detail = "ไม่อนุญาตให้ดำเนินการกับคำขอของตนเอง"
                    });
                }

                if (!rentalRequest.CurrentRevision.HasValue)
                {
                    return BadRequest(new { detail = "ไม่พบข้อมูล Revision ปัจจุบัน" });
                }

                var revisionQuery =
                    from _data in _bcg.RequestRevisions.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RevisionNumber == rentalRequest.CurrentRevision.Value
                    select _data;

                var currentRevision = await revisionQuery.SingleOrDefaultAsync(cancellationToken);

                if (currentRevision == null)
                {
                    return BadRequest(new { detail = "ไม่พบข้อมูล Revision ปัจจุบัน" });
                }

                var expectedStepNumber = rentalRequest.StatusId == DataFactory.StatusPendingManager ? 1 : 2;
                var expectedRoleId = expectedStepNumber == 1 ? DataFactory.RoleManager : DataFactory.RoleExecutive;

                var stepQuery =
                    from _data in _bcg.ApprovalSteps.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RequestRevisionId == currentRevision.Id
                        && _data.StepNumber == expectedStepNumber
                    select _data;

                var activeStep = await stepQuery.SingleOrDefaultAsync(cancellationToken);

                if (activeStep == null || activeStep.Status != DataFactory.StepStatus.StepStatusPending)
                {
                    return Conflict(new { detail = "ขั้นการอนุมัตินี้ถูกดำเนินการไปแล้ว" });
                }

                if (activeStep.ApproverId != approverId.Value || activeStep.ApprovalRoleId != expectedRoleId)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        detail = "คุณไม่ใช่ผู้อนุมัติที่ได้รับมอบหมายในขั้นตอนนี้"
                    });
                }

                var now = DateTime.Now;
                var targetStepStatus = isReject
                    ? DataFactory.StepStatus.StepStatusRejected
                    : DataFactory.StepStatus.StepStatusReturned;
                var targetRequestStatusId = isReject
                    ? DataFactory.StatusRejected
                    : DataFactory.StatusRevisionRequired;
                var targetRequestStatusCode = isReject
                    ? DataFactory.StatusCode.StatusCodeRejected
                    : DataFactory.StatusCode.StatusCodeRevisionRequired;
                var logAction = isReject
                    ? DataFactory.LogAction.LogActionReject
                    : DataFactory.LogAction.LogActionReturn;

                await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                // 1. อัปเดตขั้นปัจจุบันเป็น Rejected หรือ Returned พร้อมระบุ ActionDate และ Reason
                var updateStepQuery =
                    from _data in _bcg.ApprovalSteps
                    where _data.Active == 1
                        && _data.Id == activeStep.Id
                        && _data.Status == DataFactory.StepStatus.StepStatusPending
                        && _data.ConcurrencyVersion == activeStep.ConcurrencyVersion
                    select _data;

                var stepAffected = await updateStepQuery
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, targetStepStatus)
                        .SetProperty(s => s.ActionDate, (DateTime?)now)
                        .SetProperty(s => s.Reason, reason)
                        .SetProperty(s => s.ModifyBy, (int?)approverId.Value)
                        .SetProperty(s => s.ModifyDate, (DateTime?)now)
                        .SetProperty(s => s.ConcurrencyVersion, s => s.ConcurrencyVersion + 1),
                        cancellationToken);

                if (stepAffected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new { detail = "รายการอนุมัติถูกดำเนินการไปแล้ว กรุณารีเฟรชหน้าจอ" });
                }

                // 2. หากเป็นขั้นที่ 1 และมีขั้นที่ 2 ที่ Waiting อยู่ ให้ปรับขั้นที่ 2 เป็น Cancelled/Superseded
                var remainingWaitingStatus = isReject
                    ? DataFactory.StepStatus.StepStatusCancelled
                    : DataFactory.StepStatus.StepStatusSuperseded;

                var waitingStepQuery =
                    from _data in _bcg.ApprovalSteps
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RequestRevisionId == currentRevision.Id
                        && _data.Status == DataFactory.StepStatus.StepStatusWaiting
                    select _data;

                await waitingStepQuery
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, remainingWaitingStatus)
                        .SetProperty(s => s.ActionDate, (DateTime?)now)
                        .SetProperty(s => s.Reason, (string?)null)
                        .SetProperty(s => s.ModifyBy, (int?)approverId.Value)
                        .SetProperty(s => s.ModifyDate, (DateTime?)now)
                        .SetProperty(s => s.ConcurrencyVersion, s => s.ConcurrencyVersion + 1),
                        cancellationToken);

                // 3. อัปเดตสถานะของ RentalRequest
                var updateReqQuery =
                    from _data in _bcg.RentalRequests
                    where _data.Active == 1
                        && _data.Id == id
                        && _data.StatusId == rentalRequest.StatusId
                        && _data.ConcurrencyVersion == expectedVersion
                    select _data;

                var reqAffected = await updateReqQuery
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.StatusId, targetRequestStatusId)
                        .SetProperty(r => r.ModifyBy, (int?)approverId.Value)
                        .SetProperty(r => r.ModifyDate, (DateTime?)now)
                        .SetProperty(r => r.ConcurrencyVersion, r => r.ConcurrencyVersion + 1),
                        cancellationToken);

                if (reqAffected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new { detail = "ข้อมูลคำขอถูกแก้ไขพร้อมกัน กรุณารีเฟรชหน้าจอ" });
                }

                // 4. บันทึก Audit Log
                _bcg.Logs.Add(new Log
                {
                    RequestId = id,
                    ActorId = approverId.Value,
                    Action = logAction,
                    FromStatusId = rentalRequest.StatusId,
                    ToStatusId = targetRequestStatusId,
                    RequestRevisionId = currentRevision.Id,
                    ActionDate = now,
                    Reason = reason,
                    Details = isReject
                        ? $"ปฏิเสธคำขอค่าเช่าในขั้นที่ {expectedStepNumber} (Revision #{currentRevision.RevisionNumber})"
                        : $"ส่งคืนคำขอให้แก้ไขในขั้นที่ {expectedStepNumber} (Revision #{currentRevision.RevisionNumber})",
                    TraceId = HttpContext.TraceIdentifier,
                    Active = 1,
                    CreateBy = approverId.Value,
                    CreateDate = now
                });

                await _bcg.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return Ok(new
                {
                    requestId = id,
                    statusId = targetRequestStatusId,
                    statusCode = targetRequestStatusCode,
                    message = isReject
                        ? "ปฏิเสธคำขอค่าเช่าเรียบร้อยแล้ว"
                        : "ส่งคืนคำขอให้ผู้ขอแก้ไขเรียบร้อยแล้ว"
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะดำเนินการ {Action} คำขอ {RequestId}", isReject ? "Reject" : "Return", id);
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถดำเนินการได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }
    }
}
