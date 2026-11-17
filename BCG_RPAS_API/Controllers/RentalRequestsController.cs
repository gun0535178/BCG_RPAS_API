using System.Text.Json;
using BCG_RPAS_API.DTOs.RentalRequests;

namespace BCG_RPAS_API.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/rental-requests")]
    public class RentalRequestsController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;
        private readonly ILogger<RentalRequestsController> _logger;

        public RentalRequestsController(
            BCG_RPASContext bcg,
            GetCurrentUser getCurrentUser,
            ILogger<RentalRequestsController> logger)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
            _logger = logger;
        }

        [HttpPost]
        public async Task<ActionResult<RentalRequestResponseModel>> CreateDraft(
            [FromBody] CreateRentalRequestModel request,
            CancellationToken cancellationToken)
        {
            try
            {
                var requesterId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (requesterId == null)
                {
                    return Unauthorized();
                }

                int id = request.Id;

                // Normalize วันที่งวดให้เป็นเวลาเที่ยงคืน (00:00:00) เสมอ
                DateTime? normalizedStartDate = request.PeriodStartDate?.Date;
                DateTime? normalizedEndDate = request.PeriodEndDate?.Date;

                // เช็คยอดเงินที่จะมาเบิก (หากระบุมา ต้องมากกว่า 0)
                if (request.Amount.HasValue && request.Amount.Value <= 0)
                {
                    return BadRequest(new
                    {
                        detail = "Amount ต้องมากกว่า 0"
                    });
                }

                // เช็คอัตรา VAT (หากระบุมา ต้องไม่ติดลบ และไม่เกิน 100)
                if (request.VatRate.HasValue && (request.VatRate.Value < 0 || request.VatRate.Value > 100))
                {
                    return BadRequest(new
                    {
                        detail = "VatRate ต้องมีค่าตั้งแต่ 0 ถึง 100"
                    });
                }

                // เช็ควันที่ขอ โดยวันสิ้นสุดต้องไม่น้อยกว่าวันเริ่มเช่า
                if (normalizedStartDate is DateTime startDate &&
                    normalizedEndDate is DateTime endDate &&
                    endDate < startDate)
                {
                    return BadRequest(new
                    {
                        detail = "วันสิ้นสุดต้องไม่ก่อนวันเริ่มต้น"
                    });
                }

                // ตรวจสอบชื่อไฟล์แนบเบื้องต้น (ถ้าส่งมา)
                var cleanedFileNames = CleanAttachmentFileNames(request.AttachmentFileNames);
                if (cleanedFileNames != null && cleanedFileNames.Any(f => f.Length > 255))
                {
                    return BadRequest(new
                    {
                        detail = "ชื่อไฟล์แนบต้องมีความยาวไม่เกิน 255 ตัวอักษร"
                    });
                }

                // เช็คว่ามีฝ่าย (Cost Center) อยู่จริงไหม
                if (request.CostCenterId is int costCenterId)
                {
                    var exists = await _bcg.MasterCostCenters
                        .AsNoTracking()
                        .AnyAsync(c => c.Id == costCenterId && c.Active == 1, cancellationToken);

                    if (!exists)
                    {
                        return BadRequest(new
                        {
                            detail = "ศูนย์ต้นทุน (Cost Center) ไม่พร้อมใช้งาน"
                        });
                    }
                }

                // Vendor ID ต้องอ่านผู้ให้เช่าจากสัญญาที่เลือก
                int? vendorId = null;
                if (request.ContractId is int contractId)
                {
                    vendorId = await (
                        from c in _bcg.MasterContracts.AsNoTracking()
                        where c.Id == contractId
                            && c.Active == 1
                            && c.Vendor.Active == 1
                            && c.RentalType.Active == 1
                        select (int?)c.VendorId
                    ).SingleOrDefaultAsync(cancellationToken);

                    if (vendorId == null)
                    {
                        return BadRequest(new
                        {
                            detail = "สัญญาหรือข้อมูลผู้ให้เช่า/ประเภทค่าเช่าที่เกี่ยวข้องไม่พร้อมใช้งาน"
                        });
                    }
                }

                // ตรวจคำขอซ้ำ (ContractId + PeriodStartDate + PeriodEndDate ตรงกัน ในสถานะที่ไม่ใช่ REJECTED/CANCELLED)
                if (request.ContractId.HasValue && normalizedStartDate.HasValue && normalizedEndDate.HasValue)
                {
                    var isDuplicate = await _bcg.RentalRequests
                        .AsNoTracking()
                        .AnyAsync(r =>
                            r.Id != id &&
                            r.ContractId == request.ContractId.Value &&
                            r.PeriodStartDate == normalizedStartDate.Value &&
                            r.PeriodEndDate == normalizedEndDate.Value &&
                            r.StatusId != DataFactory.StatusRejected &&
                            r.StatusId != DataFactory.StatusCancelled,
                            cancellationToken);

                    if (isDuplicate)
                    {
                        return Conflict(new
                        {
                            detail = "มีคำขอค่าเช่าของสัญญานี้และช่วงเวลาเดียวกันอยู่ในระบบแล้ว"
                        });
                    }
                }

                var now = DateTime.UtcNow;

                // 1. สร้างคำขอใหม่ (DRAFT)
                if (id == 0)
                {
                    await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                    var rentalRequestObject = new RentalRequest
                    {
                        RequesterId = requesterId.Value,
                        ContractId = request.ContractId,
                        VendorId = vendorId,
                        CostCenterId = request.CostCenterId,
                        PeriodStartDate = normalizedStartDate,
                        PeriodEndDate = normalizedEndDate,
                        Amount = request.Amount,
                        VatRate = request.VatRate,
                        Notes = request.Notes,
                        StatusId = DataFactory.StatusDraft,
                        SyncStatus = DataFactory.SyncStatus.SyncStatusNotQueued,
                        ConcurrencyVersion = 1,
                        Active = 1,
                        CreateBy = requesterId.Value,
                        CreateDate = now
                    };

                    _bcg.RentalRequests.Add(rentalRequestObject);
                    await _bcg.SaveChangesAsync(cancellationToken);

                    // บันทึกไฟล์แนบร่าง (ถ้ามี)
                    if (cleanedFileNames != null && cleanedFileNames.Count > 0)
                    {
                        foreach (var fileName in cleanedFileNames)
                        {
                            _bcg.Attachments.Add(new Attachment
                            {
                                RequestId = rentalRequestObject.Id,
                                RequestRevisionId = null,
                                FileName = fileName,
                                Active = 1,
                                CreateBy = requesterId.Value,
                                CreateDate = now
                            });
                        }
                    }

                    // บันทึก Log การสร้างร่าง
                    _bcg.Logs.Add(new Log
                    {
                        RequestId = rentalRequestObject.Id,
                        ActorId = requesterId.Value,
                        Action = "CREATE_DRAFT",
                        FromStatusId = null,
                        ToStatusId = DataFactory.StatusDraft,
                        RequestRevisionId = null,
                        ActionDate = now,
                        Details = "สร้างคำขอค่าเช่าฉบับร่าง",
                        TraceId = HttpContext.TraceIdentifier,
                        Active = 1,
                        CreateBy = requesterId.Value,
                        CreateDate = now
                    });

                    await _bcg.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    var response = await BuildDetailResponseAsync(rentalRequestObject.Id, requesterId.Value, cancellationToken);
                    return CreatedAtAction(nameof(GetById), new { id = rentalRequestObject.Id }, response);
                }
                // 2. แก้ไขคำขอเดิม (อนุญาตเฉพาะ DRAFT และ REVISION_REQUIRED)
                else
                {
                    if (id < 0)
                    {
                        return BadRequest(new
                        {
                            detail = "Id ต้องมากกว่า 0"
                        });
                    }

                    if (request.ExpectedConcurrencyVersion is not int expectedVersion ||
                        expectedVersion <= 0 ||
                        expectedVersion == int.MaxValue)
                    {
                        return BadRequest(new
                        {
                            detail = "ต้องส่ง ExpectedConcurrencyVersion ที่ถูกต้อง"
                        });
                    }

                    var existingRequest = await _bcg.RentalRequests
                        .AsNoTracking()
                        .Where(r => r.Id == id && r.RequesterId == requesterId.Value && r.Active == 1)
                        .Select(r => new { r.Id, r.StatusId, r.ConcurrencyVersion })
                        .SingleOrDefaultAsync(cancellationToken);

                    if (existingRequest == null)
                    {
                        return NotFound(new
                        {
                            detail = "ไม่พบคำขอค่าเช่าที่ระบุ หรือไม่มีสิทธิ์เข้าถึง"
                        });
                    }

                    if (existingRequest.StatusId != DataFactory.StatusDraft &&
                        existingRequest.StatusId != DataFactory.StatusRevisionRequired)
                    {
                        return Conflict(new
                        {
                            detail = "แก้ไขได้เฉพาะคำขอในสถานะ DRAFT หรือ REVISION_REQUIRED เท่านั้น"
                        });
                    }

                    await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                    var affected = await _bcg.RentalRequests
                        .Where(r =>
                            r.Id == id &&
                            r.RequesterId == requesterId.Value &&
                            r.Active == 1 &&
                            (r.StatusId == DataFactory.StatusDraft || r.StatusId == DataFactory.StatusRevisionRequired) &&
                            r.ConcurrencyVersion == expectedVersion)
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(r => r.ContractId, request.ContractId)
                                .SetProperty(r => r.VendorId, vendorId)
                                .SetProperty(r => r.CostCenterId, request.CostCenterId)
                                .SetProperty(r => r.PeriodStartDate, normalizedStartDate)
                                .SetProperty(r => r.PeriodEndDate, normalizedEndDate)
                                .SetProperty(r => r.Amount, request.Amount)
                                .SetProperty(r => r.VatRate, request.VatRate)
                                .SetProperty(r => r.Notes, request.Notes)
                                .SetProperty(r => r.ModifyBy, (int?)requesterId.Value)
                                .SetProperty(r => r.ModifyDate, (DateTime?)now)
                                .SetProperty(r => r.ConcurrencyVersion, r => r.ConcurrencyVersion + 1),
                            cancellationToken);

                    if (affected == 0)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return Conflict(new
                        {
                            detail = "ข้อมูลถูกเปลี่ยนแปลงโดยผู้ใช้อื่น หรือสถานะไม่อนุญาตให้แก้ไข กรุณาโหลดข้อมูลใหม่"
                        });
                    }

                    // หากส่งรายการไฟล์แนบมาด้วย ให้อัปเดตรายการไฟล์แนบร่าง (เฉพาะ RequestRevisionId == null)
                    if (cleanedFileNames != null)
                    {
                        await SyncDraftAttachmentsAsync(id, requesterId.Value, cleanedFileNames, now, cancellationToken);
                    }

                    await _bcg.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    var response = await BuildDetailResponseAsync(id, requesterId.Value, cancellationToken);
                    return Ok(response);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogWarning(dbEx, "Database constraint conflict while saving draft");
                return Conflict(new
                {
                    detail = "ไม่สามารถบันทึกได้เนื่องจากสัญญาและช่วงวันที่ซ้ำกับรายการอื่นในระบบ"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะสร้างหรือแก้ไข Draft");
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถดำเนินการได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }

        [HttpPost("{id:int}/submit")]
        public async Task<ActionResult<RentalRequestResponseModel>> Submit(
            int id,
            [FromBody] SubmitRentalRequestModel request,
            CancellationToken cancellationToken)
        {
            try
            {
                var requesterId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (requesterId == null)
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

                // 1. ดึงข้อมูลคำขอปัจจุบัน
                var rentalRequest = await _bcg.RentalRequests
                    .AsNoTracking()
                    .Include(r => r.Requester)
                    .SingleOrDefaultAsync(r =>
                        r.Id == id &&
                        r.RequesterId == requesterId.Value &&
                        r.Active == 1,
                        cancellationToken);

                if (rentalRequest == null)
                {
                    return NotFound(new { detail = "ไม่พบคำขอค่าเช่าที่ระบุ หรือไม่มีสิทธิ์ดำเนินการ" });
                }

                if (rentalRequest.StatusId != DataFactory.StatusDraft &&
                    rentalRequest.StatusId != DataFactory.StatusRevisionRequired)
                {
                    return Conflict(new
                    {
                        detail = "ส่งอนุมัติได้เฉพาะคำขอที่อยู่ในสถานะ DRAFT หรือ REVISION_REQUIRED เท่านั้น"
                    });
                }

                if (rentalRequest.ConcurrencyVersion != expectedVersion)
                {
                    return Conflict(new
                    {
                        detail = "ข้อมูลคำขอมีการเปลี่ยนแปลง กรุณารีเฟรชหน้าจอแล้วลองใหม่อีกครั้ง"
                    });
                }

                // 2. ตรวจสอบความครบถ้วนของข้อมูลบังคับก่อน Submit
                if (!rentalRequest.ContractId.HasValue)
                {
                    return BadRequest(new { detail = "กรุณาเลือกเลขที่สัญญาก่อนส่งอนุมัติ" });
                }

                if (!rentalRequest.CostCenterId.HasValue)
                {
                    return BadRequest(new { detail = "กรุณาเลือกศูนย์ต้นทุน (Cost Center) ก่อนส่งอนุมัติ" });
                }

                if (!rentalRequest.PeriodStartDate.HasValue || !rentalRequest.PeriodEndDate.HasValue)
                {
                    return BadRequest(new { detail = "กรุณาระบุวันเริ่มต้นและวันสิ้นสุดงวดค่าเช่าให้ครบถ้วน" });
                }

                var periodStart = rentalRequest.PeriodStartDate.Value.Date;
                var periodEnd = rentalRequest.PeriodEndDate.Value.Date;

                if (periodEnd < periodStart)
                {
                    return BadRequest(new { detail = "วันสิ้นสุดงวดต้องไม่ก่อนวันเริ่มต้นงวด" });
                }

                if (!rentalRequest.Amount.HasValue || rentalRequest.Amount.Value <= 0)
                {
                    return BadRequest(new { detail = "ยอดเงินก่อน VAT (Amount) ต้องมากกว่า 0" });
                }

                if (!rentalRequest.VatRate.HasValue || rentalRequest.VatRate.Value < 0 || rentalRequest.VatRate.Value > 100)
                {
                    return BadRequest(new { detail = "กรุณาระบุอัตราภาษีมูลค่าเพิ่ม (VatRate) ที่ถูกต้อง (0 - 100)" });
                }

                // 3. ตรวจสอบข้อมูล Master Contract + Vendor + RentalType ว่ายัง Active และสอดคล้องกัน
                var contractInfo = await (
                    from c in _bcg.MasterContracts.AsNoTracking()
                    where c.Id == rentalRequest.ContractId.Value
                        && c.Active == 1
                        && c.Vendor.Active == 1
                        && c.RentalType.Active == 1
                    select new
                    {
                        c.Id,
                        c.ContractNumber,
                        c.VendorId,
                        VendorCode = c.Vendor.VendorCode,
                        VendorName = c.Vendor.Name,
                        c.RentalTypeId,
                        RentalTypeCode = c.RentalType.RentalTypeCode,
                        RentalTypeName = c.RentalType.Name
                    }
                ).SingleOrDefaultAsync(cancellationToken);

                if (contractInfo == null)
                {
                    return BadRequest(new
                    {
                        detail = "สัญญา ผู้ให้เช่า หรือประเภทค่าเช่าที่เลือกไม่พร้อมใช้งานในระบบ"
                    });
                }

                // ตรวจสอบข้อมูล Master CostCenter
                var costCenterInfo = await (
                    from cc in _bcg.MasterCostCenters.AsNoTracking()
                    where cc.Id == rentalRequest.CostCenterId.Value && cc.Active == 1
                    select new
                    {
                        cc.Id,
                        cc.CostCenterCode,
                        cc.Name
                    }
                ).SingleOrDefaultAsync(cancellationToken);

                if (costCenterInfo == null)
                {
                    return BadRequest(new { detail = "ศูนย์ต้นทุน (Cost Center) ที่เลือกไม่พร้อมใช้งาน" });
                }

                // 4. ตรวจคำขอซ้ำ (ตัด RequestId ปัจจุบันออก)
                var hasDuplicate = await _bcg.RentalRequests
                    .AsNoTracking()
                    .AnyAsync(r =>
                        r.Id != id &&
                        r.ContractId == contractInfo.Id &&
                        r.PeriodStartDate == periodStart &&
                        r.PeriodEndDate == periodEnd &&
                        r.StatusId != DataFactory.StatusRejected &&
                        r.StatusId != DataFactory.StatusCancelled,
                        cancellationToken);

                if (hasDuplicate)
                {
                    return Conflict(new
                    {
                        detail = "ไม่สามารถส่งอนุมัติได้ เนื่องจากมีคำขอของสัญญานี้และงวดวันที่เดียวกันอยู่แล้ว"
                    });
                }

                // 5. ตรวจสอบรายการชื่อไฟล์แนบ (อย่างน้อย 1 รายการ)
                var incomingFiles = CleanAttachmentFileNames(request.AttachmentFileNames);
                List<string> activeDraftFiles;

                if (incomingFiles != null)
                {
                    if (incomingFiles.Any(f => f.Length > 255))
                    {
                        return BadRequest(new { detail = "ชื่อไฟล์แนบต้องมีความยาวไม่เกิน 255 ตัวอักษร" });
                    }
                    activeDraftFiles = incomingFiles;
                }
                else
                {
                    activeDraftFiles = await _bcg.Attachments
                        .AsNoTracking()
                        .Where(a => a.RequestId == id && a.RequestRevisionId == null && a.Active == 1)
                        .OrderBy(a => a.Id)
                        .Select(a => a.FileName)
                        .ToListAsync(cancellationToken);
                }

                if (activeDraftFiles.Count == 0)
                {
                    return BadRequest(new
                    {
                        detail = "กรุณาระบุชื่อไฟล์เอกสารแนบอย่างน้อย 1 รายการก่อนส่งอนุมัติ"
                    });
                }

                // 6. ตรวจสอบสายอนุมัติ (ApprovalAssignments) และป้องกันการอนุมัติตนเอง
                var amount = rentalRequest.Amount.Value;
                var vatRate = rentalRequest.VatRate.Value;
                var requiresExecutive = amount > DataFactory.ManagerApprovalThreshold;

                var assignments = await (
                    from aa in _bcg.ApprovalAssignments.AsNoTracking()
                    where aa.RequesterId == requesterId.Value && aa.Active == 1
                    select new
                    {
                        aa.ApprovalRoleId,
                        aa.ApproverId,
                        ApproverUserName = aa.Approver.UserName,
                        ApproverDisplayName = aa.Approver.DisplayName,
                        ApproverActive = aa.Approver.Active,
                        ApproverCanLogin = aa.Approver.CanLogin,
                        ApproverRoleId = aa.Approver.RoleId,
                        ApproverRoleActive = aa.Approver.Role.Active,
                        ApproverRoleCode = aa.Approver.Role.RoleCode
                    }
                ).ToListAsync(cancellationToken);

                var managerAssignment = assignments.SingleOrDefault(a => a.ApprovalRoleId == DataFactory.RoleManager);
                if (managerAssignment == null ||
                    managerAssignment.ApproverActive != 1 ||
                    managerAssignment.ApproverCanLogin != 1 ||
                    managerAssignment.ApproverRoleActive != 1 ||
                    managerAssignment.ApproverRoleId != DataFactory.RoleManager)
                {
                    return BadRequest(new
                    {
                        detail = "ไม่พบการตั้งค่าหัวหน้าผู้อนุมัติ (Manager) ที่พร้อมใช้งานสำหรับผู้ขอรายนี้"
                    });
                }

                if (managerAssignment.ApproverId == requesterId.Value)
                {
                    return BadRequest(new
                    {
                        detail = "ผู้ขอไม่สามารถอนุมัติคำขอของตนเองในบทบาทหัวหน้า (Manager) ได้"
                    });
                }

                var executiveAssignment = assignments.SingleOrDefault(a => a.ApprovalRoleId == DataFactory.RoleExecutive);
                if (requiresExecutive)
                {
                    if (executiveAssignment == null ||
                        executiveAssignment.ApproverActive != 1 ||
                        executiveAssignment.ApproverCanLogin != 1 ||
                        executiveAssignment.ApproverRoleActive != 1 ||
                        executiveAssignment.ApproverRoleId != DataFactory.RoleExecutive)
                    {
                        return BadRequest(new
                        {
                            detail = "คำขอมียอดก่อน VAT เกิน 50,000 บาท แต่ไม่พบการตั้งค่าผู้บริหาร (Executive) ที่พร้อมใช้งาน"
                        });
                    }

                    if (executiveAssignment.ApproverId == requesterId.Value)
                    {
                        return BadRequest(new
                        {
                            detail = "ผู้ขอไม่สามารถอนุมัติคำขอของตนเองในบทบาทผู้บริหาร (Executive) ได้"
                        });
                    }

                    if (executiveAssignment.ApproverId == managerAssignment.ApproverId)
                    {
                        return BadRequest(new
                        {
                            detail = "ผู้อนุมัติขั้นต้น (Manager) และผู้บริหาร (Executive) ต้องไม่เป็นบุคคลเดียวกัน"
                        });
                    }
                }

                // 7. บันทึก Revision, Snapshot, ApprovalSteps, Attachments และเปลี่ยนสถานะแบบ Atomic
                var now = DateTime.UtcNow;
                var nextRevisionNumber = (rentalRequest.CurrentRevision ?? 0) + 1;
                var isResubmit = rentalRequest.StatusId == DataFactory.StatusRevisionRequired;

                var computedVatAmount = Math.Round(amount * vatRate / 100m, 2, MidpointRounding.AwayFromZero);
                var computedTotalAmount = amount + computedVatAmount;

                var approvalPlanSnapshot = new List<object>
                {
                    new
                    {
                        StepNumber = 1,
                        ApprovalRoleId = DataFactory.RoleManager,
                        RoleCode = DataFactory.RoleCode.RoleCodeManager,
                        ApproverId = managerAssignment.ApproverId,
                        ApproverUserName = managerAssignment.ApproverUserName,
                        ApproverDisplayName = managerAssignment.ApproverDisplayName,
                        InitialStatus = DataFactory.StepStatus.StepStatusPending
                    }
                };

                if (requiresExecutive && executiveAssignment != null)
                {
                    approvalPlanSnapshot.Add(new
                    {
                        StepNumber = 2,
                        ApprovalRoleId = DataFactory.RoleExecutive,
                        RoleCode = DataFactory.RoleCode.RoleCodeExecutive,
                        ApproverId = executiveAssignment.ApproverId,
                        ApproverUserName = executiveAssignment.ApproverUserName,
                        ApproverDisplayName = executiveAssignment.ApproverDisplayName,
                        InitialStatus = DataFactory.StepStatus.StepStatusWaiting
                    });
                }

                var snapshotPayload = new
                {
                    RequestId = id,
                    RevisionNumber = nextRevisionNumber,
                    SubmittedDate = now,
                    Status = new
                    {
                        StatusId = DataFactory.StatusPendingManager,
                        StatusCode = DataFactory.StatusCode.StatusCodePendingManager,
                        Name = "รอผู้จัดการอนุมัติ"
                    },
                    Requester = new
                    {
                        Id = rentalRequest.Requester.Id,
                        UserName = rentalRequest.Requester.UserName,
                        DisplayName = rentalRequest.Requester.DisplayName,
                        Email = rentalRequest.Requester.Email
                    },
                    Contract = new
                    {
                        Id = contractInfo.Id,
                        ContractNumber = contractInfo.ContractNumber,
                        RentalTypeId = contractInfo.RentalTypeId,
                        RentalTypeCode = contractInfo.RentalTypeCode,
                        RentalTypeName = contractInfo.RentalTypeName
                    },
                    Vendor = new
                    {
                        Id = contractInfo.VendorId,
                        VendorCode = contractInfo.VendorCode,
                        Name = contractInfo.VendorName
                    },
                    CostCenter = new
                    {
                        Id = costCenterInfo.Id,
                        CostCenterCode = costCenterInfo.CostCenterCode,
                        Name = costCenterInfo.Name
                    },
                    PeriodStartDate = periodStart,
                    PeriodEndDate = periodEnd,
                    Amount = amount,
                    VatRate = vatRate,
                    VatAmount = computedVatAmount,
                    TotalAmount = computedTotalAmount,
                    Notes = rentalRequest.Notes,
                    Attachments = activeDraftFiles,
                    ApprovalSteps = approvalPlanSnapshot
                };

                var snapshotJson = JsonSerializer.Serialize(snapshotPayload);

                await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                // หากส่งรายการไฟล์แนบเข้ามาใหม่ตอน Submit ให้ซิงค์รายการไฟล์ร่างด้วย
                if (incomingFiles != null)
                {
                    await SyncDraftAttachmentsAsync(id, requesterId.Value, activeDraftFiles, now, cancellationToken);
                }

                // ยกเลิก Step เก่าของ Revision ก่อนหน้าที่อาจค้างอยู่ให้เป็น Superseded
                await _bcg.ApprovalSteps
                    .Where(s =>
                        s.RequestId == id &&
                        s.Active == 1 &&
                        (s.Status == DataFactory.StepStatus.StepStatusPending ||
                         s.Status == DataFactory.StepStatus.StepStatusWaiting))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, DataFactory.StepStatus.StepStatusSuperseded)
                        .SetProperty(s => s.ModifyBy, (int?)requesterId.Value)
                        .SetProperty(s => s.ModifyDate, (DateTime?)now)
                        .SetProperty(s => s.ConcurrencyVersion, s => s.ConcurrencyVersion + 1),
                        cancellationToken);

                // สร้าง RequestRevision ใหม่ก่อน เพื่อนำไปอ้างอิง FK_RentalRequests_CurrentRevision
                var newRevision = new RequestRevision
                {
                    RequestId = id,
                    RevisionNumber = nextRevisionNumber,
                    ContractId = contractInfo.Id,
                    VendorId = contractInfo.VendorId,
                    CostCenterId = costCenterInfo.Id,
                    PeriodStartDate = periodStart,
                    PeriodEndDate = periodEnd,
                    Amount = amount,
                    VatRate = vatRate,
                    SnapshotJson = snapshotJson,
                    SubmittedDate = now,
                    Active = 1,
                    CreateBy = requesterId.Value,
                    CreateDate = now
                };

                _bcg.RequestRevisions.Add(newRevision);
                await _bcg.SaveChangesAsync(cancellationToken);

                // สร้าง Snapshot Attachments ผูกกับ RequestRevisionId ใหม่
                foreach (var fileName in activeDraftFiles)
                {
                    _bcg.Attachments.Add(new Attachment
                    {
                        RequestId = id,
                        RequestRevisionId = newRevision.Id,
                        FileName = fileName,
                        Active = 1,
                        CreateBy = requesterId.Value,
                        CreateDate = now
                    });
                }

                // สร้าง ApprovalSteps สำหรับ Revision นี้
                _bcg.ApprovalSteps.Add(new ApprovalStep
                {
                    RequestId = id,
                    RequestRevisionId = newRevision.Id,
                    StepNumber = 1,
                    ApprovalRoleId = DataFactory.RoleManager,
                    ApproverId = managerAssignment.ApproverId,
                    Status = DataFactory.StepStatus.StepStatusPending,
                    ConcurrencyVersion = 1,
                    Active = 1,
                    CreateBy = requesterId.Value,
                    CreateDate = now
                });

                if (requiresExecutive && executiveAssignment != null)
                {
                    _bcg.ApprovalSteps.Add(new ApprovalStep
                    {
                        RequestId = id,
                        RequestRevisionId = newRevision.Id,
                        StepNumber = 2,
                        ApprovalRoleId = DataFactory.RoleExecutive,
                        ApproverId = executiveAssignment.ApproverId,
                        Status = DataFactory.StepStatus.StepStatusWaiting,
                        ConcurrencyVersion = 1,
                        Active = 1,
                        CreateBy = requesterId.Value,
                        CreateDate = now
                    });
                }

                // อัปเดตสถานะ RentalRequest และ ConcurrencyVersion แบบ Atomic
                var affected = await _bcg.RentalRequests
                    .Where(r =>
                        r.Id == id &&
                        r.RequesterId == requesterId.Value &&
                        r.Active == 1 &&
                        (r.StatusId == DataFactory.StatusDraft || r.StatusId == DataFactory.StatusRevisionRequired) &&
                        r.ConcurrencyVersion == expectedVersion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.VendorId, (int?)contractInfo.VendorId)
                        .SetProperty(r => r.PeriodStartDate, (DateTime?)periodStart)
                        .SetProperty(r => r.PeriodEndDate, (DateTime?)periodEnd)
                        .SetProperty(r => r.StatusId, DataFactory.StatusPendingManager)
                        .SetProperty(r => r.CurrentRevision, (int?)nextRevisionNumber)
                        .SetProperty(r => r.SubmittedDate, (DateTime?)now)
                        .SetProperty(r => r.ModifyBy, (int?)requesterId.Value)
                        .SetProperty(r => r.ModifyDate, (DateTime?)now)
                        .SetProperty(r => r.ConcurrencyVersion, r => r.ConcurrencyVersion + 1),
                        cancellationToken);

                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new
                    {
                        detail = "คำขอถูกแก้ไขจากช่องทางอื่นในเวลาเดียวกัน กรุณารีเฟรชและลองใหม่อีกครั้ง"
                    });
                }

                // บันทึก Audit Log
                _bcg.Logs.Add(new Log
                {
                    RequestId = id,
                    ActorId = requesterId.Value,
                    Action = isResubmit ? "RESUBMIT" : "SUBMIT",
                    FromStatusId = rentalRequest.StatusId,
                    ToStatusId = DataFactory.StatusPendingManager,
                    RequestRevisionId = newRevision.Id,
                    ActionDate = now,
                    Details = isResubmit
                        ? $"ส่งคำขออนุมัติใหม่ (Revision #{nextRevisionNumber})"
                        : $"ส่งคำขออนุมัติครั้งแรก (Revision #{nextRevisionNumber})",
                    TraceId = HttpContext.TraceIdentifier,
                    Active = 1,
                    CreateBy = requesterId.Value,
                    CreateDate = now
                });

                await _bcg.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                var response = await BuildDetailResponseAsync(id, requesterId.Value, cancellationToken);
                return Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogWarning(dbEx, "Database constraint conflict while submitting request {RequestId}", id);
                return Conflict(new
                {
                    detail = "ไม่สามารถส่งอนุมัติได้เนื่องจากสัญญาและงวดวันที่ซ้ำกับรายการอื่นในระบบ"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะ Submit คำขอ {RequestId}", id);
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถส่งอนุมัติได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }

        [HttpPost("{id:int}/cancel")]
        public async Task<ActionResult<RentalRequestResponseModel>> Cancel(
            int id,
            [FromBody] CancelRentalRequestModel request,
            CancellationToken cancellationToken)
        {
            try
            {
                var requesterId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (requesterId == null)
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
                    return BadRequest(new { detail = "กรุณาระบุเหตุผลการยกเลิกคำขอ" });
                }

                if (request.ExpectedConcurrencyVersion is not int expectedVersion ||
                    expectedVersion <= 0 ||
                    expectedVersion == int.MaxValue)
                {
                    return BadRequest(new { detail = "ต้องส่ง ExpectedConcurrencyVersion ที่ถูกต้อง" });
                }

                var rentalRequest = await _bcg.RentalRequests
                    .AsNoTracking()
                    .Where(r => r.Id == id && r.RequesterId == requesterId.Value && r.Active == 1)
                    .Select(r => new
                    {
                        r.Id,
                        r.StatusId,
                        r.CurrentRevision,
                        r.ConcurrencyVersion
                    })
                    .SingleOrDefaultAsync(cancellationToken);

                if (rentalRequest == null)
                {
                    return NotFound(new { detail = "ไม่พบคำขอค่าเช่าที่ระบุ หรือไม่มีสิทธิ์ดำเนินการ" });
                }

                // อนุญาตให้เจ้าของยกเลิกได้เฉพาะก่อน APPROVED (DRAFT, PENDING_MANAGER, PENDING_EXECUTIVE, REVISION_REQUIRED)
                if (rentalRequest.StatusId != DataFactory.StatusDraft &&
                    rentalRequest.StatusId != DataFactory.StatusPendingManager &&
                    rentalRequest.StatusId != DataFactory.StatusPendingExecutive &&
                    rentalRequest.StatusId != DataFactory.StatusRevisionRequired)
                {
                    return Conflict(new
                    {
                        detail = "ไม่สามารถยกเลิกคำขอที่ได้รับการอนุมัติ ถูกปฏิเสธ หรือยกเลิกไปแล้วได้"
                    });
                }

                int? currentRevisionId = null;
                if (rentalRequest.CurrentRevision.HasValue)
                {
                    currentRevisionId = await _bcg.RequestRevisions
                        .AsNoTracking()
                        .Where(rv => rv.RequestId == id && rv.RevisionNumber == rentalRequest.CurrentRevision.Value)
                        .Select(rv => (int?)rv.Id)
                        .SingleOrDefaultAsync(cancellationToken);
                }

                var now = DateTime.UtcNow;
                await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                var affected = await _bcg.RentalRequests
                    .Where(r =>
                        r.Id == id &&
                        r.RequesterId == requesterId.Value &&
                        r.Active == 1 &&
                        (r.StatusId == DataFactory.StatusDraft ||
                         r.StatusId == DataFactory.StatusPendingManager ||
                         r.StatusId == DataFactory.StatusPendingExecutive ||
                         r.StatusId == DataFactory.StatusRevisionRequired) &&
                        r.ConcurrencyVersion == expectedVersion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.StatusId, DataFactory.StatusCancelled)
                        .SetProperty(r => r.CancellationReason, reason)
                        .SetProperty(r => r.ModifyBy, (int?)requesterId.Value)
                        .SetProperty(r => r.ModifyDate, (DateTime?)now)
                        .SetProperty(r => r.ConcurrencyVersion, r => r.ConcurrencyVersion + 1),
                        cancellationToken);

                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Conflict(new
                    {
                        detail = "ข้อมูลคำขอถูกเปลี่ยนแปลงไปแล้ว กรุณารีเฟรชและลองใหม่อีกครั้ง"
                    });
                }

                // ปรับสถานะ ApprovalSteps ที่ค้างอยู่ในรอบปัจจุบันเป็น Cancelled
                await _bcg.ApprovalSteps
                    .Where(s =>
                        s.RequestId == id &&
                        s.Active == 1 &&
                        (s.Status == DataFactory.StepStatus.StepStatusPending ||
                         s.Status == DataFactory.StepStatus.StepStatusWaiting))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, DataFactory.StepStatus.StepStatusCancelled)
                        .SetProperty(s => s.ActionDate, (DateTime?)now)
                        .SetProperty(s => s.Reason, reason)
                        .SetProperty(s => s.ModifyBy, (int?)requesterId.Value)
                        .SetProperty(s => s.ModifyDate, (DateTime?)now)
                        .SetProperty(s => s.ConcurrencyVersion, s => s.ConcurrencyVersion + 1),
                        cancellationToken);

                _bcg.Logs.Add(new Log
                {
                    RequestId = id,
                    ActorId = requesterId.Value,
                    Action = "CANCEL",
                    FromStatusId = rentalRequest.StatusId,
                    ToStatusId = DataFactory.StatusCancelled,
                    RequestRevisionId = currentRevisionId,
                    ActionDate = now,
                    Reason = reason,
                    Details = "ผู้ขอทำการยกเลิกคำขอค่าเช่า",
                    TraceId = HttpContext.TraceIdentifier,
                    Active = 1,
                    CreateBy = requesterId.Value,
                    CreateDate = now
                });

                await _bcg.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                var response = await BuildDetailResponseAsync(id, requesterId.Value, cancellationToken);
                return Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะยกเลิกคำขอ {RequestId}", id);
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถยกเลิกคำขอได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<RentalRequestResponseModel>> GetById(
            int id,
            CancellationToken cancellationToken)
        {
            try
            {
                var currentUserId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (currentUserId == null)
                {
                    return Unauthorized();
                }

                // ตรวจสอบสิทธิ์การเข้าดู: เจ้าของคำขอ, ผู้อนุมัติที่เกี่ยวข้องกับคำขอนี้, หรือผู้ดูแลระบบ (ADMINISTRATOR)
                var isAdmin = User.IsInRole(DataFactory.RoleCode.RoleCodeAdministrator);
                var canAccess = await _bcg.RentalRequests
                    .AsNoTracking()
                    .AnyAsync(r =>
                        r.Id == id &&
                        r.Active == 1 &&
                        (isAdmin ||
                         r.RequesterId == currentUserId.Value ||
                         r.ApprovalSteps.Any(s => s.ApproverId == currentUserId.Value && s.Active == 1)),
                        cancellationToken);

                if (!canAccess)
                {
                    return NotFound();
                }

                var result = await BuildDetailResponseAsync(id, currentUserId.Value, cancellationToken);
                if (result == null)
                {
                    return NotFound();
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ไม่สามารถอ่าน RentalRequest {RequestId}", id);
                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถดำเนินการได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }

        private static List<string>? CleanAttachmentFileNames(List<string>? rawNames)
        {
            if (rawNames == null)
            {
                return null;
            }

            return rawNames
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task SyncDraftAttachmentsAsync(
            int requestId,
            int actorId,
            List<string> fileNames,
            DateTime now,
            CancellationToken cancellationToken)
        {
            // ปิดการใช้งานรายการไฟล์แนบร่างเดิม (เฉพาะที่ RequestRevisionId == null)
            await _bcg.Attachments
                .Where(a => a.RequestId == requestId && a.RequestRevisionId == null && a.Active == 1)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(a => a.Active, 0)
                    .SetProperty(a => a.ModifyBy, (int?)actorId)
                    .SetProperty(a => a.ModifyDate, (DateTime?)now),
                    cancellationToken);

            // เพิ่มรายการไฟล์แนบร่างชุดปัจจุบัน
            foreach (var fileName in fileNames)
            {
                _bcg.Attachments.Add(new Attachment
                {
                    RequestId = requestId,
                    RequestRevisionId = null,
                    FileName = fileName,
                    Active = 1,
                    CreateBy = actorId,
                    CreateDate = now
                });
            }
        }

        private async Task<RentalRequestResponseModel?> BuildDetailResponseAsync(
            int id,
            int viewerUserId,
            CancellationToken cancellationToken)
        {
            var header = await (
                from _data in _bcg.RentalRequests.AsNoTracking()
                where _data.Id == id && _data.Active == 1
                select new RentalRequestResponseModel
                {
                    Id = _data.Id,
                    RequesterId = _data.RequesterId,
                    RequesterName = _data.Requester.DisplayName,
                    ContractId = _data.ContractId,
                    ContractNumber = _data.MasterContract != null ? _data.MasterContract.ContractNumber : null,
                    VendorId = _data.VendorId,
                    VendorName = _data.Vendor != null ? _data.Vendor.Name : null,
                    RentalTypeId = _data.MasterContract != null ? (int?)_data.MasterContract.RentalTypeId : null,
                    RentalTypeName = _data.MasterContract != null ? _data.MasterContract.RentalType.Name : null,
                    CostCenterId = _data.CostCenterId,
                    CostCenterCode = _data.CostCenter != null ? _data.CostCenter.CostCenterCode : null,
                    CostCenterName = _data.CostCenter != null ? _data.CostCenter.Name : null,
                    PeriodStartDate = _data.PeriodStartDate,
                    PeriodEndDate = _data.PeriodEndDate,
                    Amount = _data.Amount,
                    VatRate = _data.VatRate,
                    VatAmount = _data.VatAmount,
                    TotalAmount = _data.TotalAmount,
                    Notes = _data.Notes,
                    CancellationReason = _data.CancellationReason,
                    StatusId = _data.StatusId,
                    StatusCode = _data.Status.StatusCode,
                    StatusName = _data.Status.Name,
                    SyncStatus = _data.SyncStatus,
                    CurrentRevision = _data.CurrentRevision,
                    SubmittedDate = _data.SubmittedDate,
                    ApprovedDate = _data.ApprovedDate,
                    ConcurrencyVersion = _data.ConcurrencyVersion,
                    CreateDate = _data.CreateDate
                }
            ).SingleOrDefaultAsync(cancellationToken);

            if (header == null)
            {
                return null;
            }

            // ดึงไฟล์แนบ: หากอยู่ใน DRAFT หรือ REVISION_REQUIRED ให้แสดงรายการร่างล่าสุด มิฉะนั้นแสดงตาม Revision ปัจจุบัน
            int? activeRevisionId = null;
            if (header.CurrentRevision.HasValue)
            {
                activeRevisionId = await _bcg.RequestRevisions
                    .AsNoTracking()
                    .Where(rv => rv.RequestId == id && rv.RevisionNumber == header.CurrentRevision.Value && rv.Active == 1)
                    .Select(rv => (int?)rv.Id)
                    .SingleOrDefaultAsync(cancellationToken);
            }

            if (header.StatusId == DataFactory.StatusDraft ||
                header.StatusId == DataFactory.StatusRevisionRequired ||
                !activeRevisionId.HasValue)
            {
                header.AttachmentFileNames = await _bcg.Attachments
                    .AsNoTracking()
                    .Where(a => a.RequestId == id && a.RequestRevisionId == null && a.Active == 1)
                    .OrderBy(a => a.Id)
                    .Select(a => a.FileName)
                    .ToListAsync(cancellationToken);
            }
            else
            {
                header.AttachmentFileNames = await _bcg.Attachments
                    .AsNoTracking()
                    .Where(a => a.RequestId == id && a.RequestRevisionId == activeRevisionId.Value && a.Active == 1)
                    .OrderBy(a => a.Id)
                    .Select(a => a.FileName)
                    .ToListAsync(cancellationToken);
            }

            // ดึงลำดับขั้นการอนุมัติของ Revision ล่าสุด (ถ้ามี)
            if (activeRevisionId.HasValue)
            {
                header.ApprovalSteps = await (
                    from s in _bcg.ApprovalSteps.AsNoTracking()
                    where s.RequestId == id && s.RequestRevisionId == activeRevisionId.Value && s.Active == 1
                    orderby s.StepNumber
                    select new ApprovalStepItemModel
                    {
                        StepNumber = s.StepNumber,
                        ApprovalRoleId = s.ApprovalRoleId,
                        RoleCode = s.ApprovalRole.RoleCode,
                        RoleName = s.ApprovalRole.Name,
                        ApproverId = s.ApproverId,
                        ApproverName = s.Approver.DisplayName,
                        Status = s.Status,
                        ActionDate = s.ActionDate,
                        Reason = s.Reason
                    }
                ).ToListAsync(cancellationToken);
            }

            // ดึงประวัติ Timeline (Logs)
            header.Logs = await (
                from l in _bcg.Logs.AsNoTracking()
                where l.RequestId == id && l.Active == 1
                orderby l.ActionDate descending, l.Id descending
                select new RequestLogItemModel
                {
                    Id = l.Id,
                    Action = l.Action,
                    ActorId = l.ActorId,
                    ActorName = l.Actor.DisplayName,
                    FromStatusCode = l.FromStatus != null ? l.FromStatus.StatusCode : null,
                    ToStatusCode = l.ToStatus.StatusCode,
                    RequestRevisionId = l.RequestRevisionId,
                    ActionDate = l.ActionDate,
                    Reason = l.Reason,
                    Details = l.Details
                }
            ).ToListAsync(cancellationToken);

            return header;
        }
    }
}
