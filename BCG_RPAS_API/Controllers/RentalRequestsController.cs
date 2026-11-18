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

                // เช็คว่ามีฝ่าย/แผนก (Cost Center) อยู่จริงไหม
                if (request.CostCenterId is int costCenterId)
                {
                    var costCenterQuery =
                        from _data in _bcg.MasterCostCenters.AsNoTracking()
                        where _data.Active == 1
                            && _data.Id == costCenterId
                        select _data.Id;

                    var exists = await costCenterQuery.AnyAsync(cancellationToken);

                    if (!exists)
                    {
                        return BadRequest(new
                        {
                            detail = "แผนก/ฝ่าย (Cost Center) ไม่พร้อมใช้งาน"
                        });
                    }
                }

                // Vendor ID ต้องอ่านผู้ให้เช่าจากสัญญาที่เลือก
                int? vendorId = null;
                if (request.ContractId is int contractId)
                {
                    var vendorQuery =
                        from _data in _bcg.MasterContracts.AsNoTracking()
                        where _data.Active == 1
                            && _data.Id == contractId
                            && _data.Vendor.Active == 1
                            && _data.RentalType.Active == 1
                        select (int?)_data.VendorId;

                    vendorId = await vendorQuery.SingleOrDefaultAsync(cancellationToken);

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
                    var duplicateQuery =
                        from _data in _bcg.RentalRequests.AsNoTracking()
                        where _data.Active == 1
                            && _data.Id != id
                            && _data.ContractId == request.ContractId.Value
                            && _data.PeriodStartDate == normalizedStartDate.Value
                            && _data.PeriodEndDate == normalizedEndDate.Value
                            && _data.StatusId != DataFactory.StatusRejected
                            && _data.StatusId != DataFactory.StatusCancelled
                        select _data.Id;

                    var isDuplicate = await duplicateQuery.AnyAsync(cancellationToken);

                    if (isDuplicate)
                    {
                        return Conflict(new
                        {
                            detail = "มีคำขอค่าเช่าของสัญญานี้และช่วงเวลาเดียวกันอยู่ในระบบแล้ว"
                        });
                    }
                }

                var now = DateTime.Now;

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

                    // บันทึก Log การสร้างร่าง (ต้องตรงกับ CK_Logs_Action = 'Create')
                    _bcg.Logs.Add(new Log
                    {
                        RequestId = rentalRequestObject.Id,
                        ActorId = requesterId.Value,
                        Action = DataFactory.LogAction.LogActionCreate,
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

                    var existingQuery =
                        from _data in _bcg.RentalRequests.AsNoTracking()
                        where _data.Active == 1
                            && _data.Id == id
                            && _data.RequesterId == requesterId.Value
                        select new
                        {
                            _data.Id,
                            _data.StatusId,
                            _data.ConcurrencyVersion,
                            _data.ContractId,
                            ContractNumber = _data.MasterContract != null ? _data.MasterContract.ContractNumber : null,
                            _data.CostCenterId,
                            CostCenterCode = _data.CostCenter != null ? _data.CostCenter.CostCenterCode : null,
                            _data.PeriodStartDate,
                            _data.PeriodEndDate,
                            _data.Amount,
                            _data.VatRate,
                            _data.Notes
                        };

                    var existingRequest = await existingQuery.SingleOrDefaultAsync(cancellationToken);

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
                            detail = "แก้ไขได้เฉพาะคำขอในสถานะฉบับร่าง หรือ ส่งคืนให้แก้ไข เท่านั้น"
                        });
                    }

                    // ดึงรายการไฟล์แนบร่างเดิมก่อนแก้ไข เพื่อเปรียบเทียบว่าแก้ไขอะไรไปบ้าง
                    var oldFilesQuery =
                        from _data in _bcg.Attachments.AsNoTracking()
                        where _data.Active == 1
                            && _data.RequestId == id
                            && _data.RequestRevisionId == null
                        orderby _data.Id
                        select _data.FileName;

                    var oldFileNames = await oldFilesQuery.ToListAsync(cancellationToken);

                    // สร้างข้อความสรุปการแก้ไข (จากอะไร -> เป็นอะไร)
                    var changes = new List<string>();

                    if (existingRequest.ContractId != request.ContractId)
                    {
                        string newContractLabel = "-";
                        if (request.ContractId.HasValue)
                        {
                            var newContractQuery =
                                from _data in _bcg.MasterContracts.AsNoTracking()
                                where _data.Id == request.ContractId.Value
                                select _data.ContractNumber;
                            newContractLabel = await newContractQuery.SingleOrDefaultAsync(cancellationToken) ?? $"#{request.ContractId.Value}";
                        }
                        var oldContractLabel = existingRequest.ContractNumber ?? (existingRequest.ContractId.HasValue ? $"#{existingRequest.ContractId.Value}" : "-");
                        changes.Add($"เลขที่สัญญา (จาก '{oldContractLabel}' เป็น '{newContractLabel}')");
                    }

                    if (existingRequest.CostCenterId != request.CostCenterId)
                    {
                        string newCcLabel = "-";
                        if (request.CostCenterId.HasValue)
                        {
                            var newCcQuery =
                                from _data in _bcg.MasterCostCenters.AsNoTracking()
                                where _data.Id == request.CostCenterId.Value
                                select _data.CostCenterCode;
                            newCcLabel = await newCcQuery.SingleOrDefaultAsync(cancellationToken) ?? $"#{request.CostCenterId.Value}";
                        }
                        var oldCcLabel = existingRequest.CostCenterCode ?? (existingRequest.CostCenterId.HasValue ? $"#{existingRequest.CostCenterId.Value}" : "-");
                        changes.Add($"แผนก/ฝ่าย (จาก '{oldCcLabel}' เป็น '{newCcLabel}')");
                    }

                    if (existingRequest.PeriodStartDate?.Date != normalizedStartDate)
                    {
                        var oldStart = existingRequest.PeriodStartDate?.ToString("dd/MM/yyyy") ?? "-";
                        var newStart = normalizedStartDate?.ToString("dd/MM/yyyy") ?? "-";
                        changes.Add($"วันเริ่มต้นงวด (จาก '{oldStart}' เป็น '{newStart}')");
                    }

                    if (existingRequest.PeriodEndDate?.Date != normalizedEndDate)
                    {
                        var oldEnd = existingRequest.PeriodEndDate?.ToString("dd/MM/yyyy") ?? "-";
                        var newEnd = normalizedEndDate?.ToString("dd/MM/yyyy") ?? "-";
                        changes.Add($"วันสิ้นสุดงวด (จาก '{oldEnd}' เป็น '{newEnd}')");
                    }

                    if (existingRequest.Amount != request.Amount)
                    {
                        var oldAmt = existingRequest.Amount?.ToString("N2") ?? "-";
                        var newAmt = request.Amount?.ToString("N2") ?? "-";
                        changes.Add($"ยอดเงินก่อน VAT (จาก '{oldAmt}' เป็น '{newAmt}' บาท)");
                    }

                    if (existingRequest.VatRate != request.VatRate)
                    {
                        var oldVat = existingRequest.VatRate?.ToString("N2") ?? "-";
                        var newVat = request.VatRate?.ToString("N2") ?? "-";
                        changes.Add($"อัตรา VAT (จาก '{oldVat}%' เป็น '{newVat}%')");
                    }

                    var oldNotesNorm = string.IsNullOrWhiteSpace(existingRequest.Notes) ? "-" : existingRequest.Notes.Trim();
                    var newNotesNorm = string.IsNullOrWhiteSpace(request.Notes) ? "-" : request.Notes.Trim();
                    if (!string.Equals(oldNotesNorm, newNotesNorm, StringComparison.Ordinal))
                    {
                        changes.Add($"หมายเหตุ (จาก '{oldNotesNorm}' เป็น '{newNotesNorm}')");
                    }

                    if (cleanedFileNames != null)
                    {
                        var oldFilesJoined = oldFileNames.Count > 0 ? string.Join(", ", oldFileNames) : "ไม่มีไฟล์";
                        var newFilesJoined = cleanedFileNames.Count > 0 ? string.Join(", ", cleanedFileNames) : "ไม่มีไฟล์";
                        if (!string.Equals(oldFilesJoined, newFilesJoined, StringComparison.OrdinalIgnoreCase))
                        {
                            changes.Add($"ไฟล์เอกสารแนบ (จาก '{oldFilesJoined}' เป็น '{newFilesJoined}')");
                        }
                    }

                    var editLogDetails = changes.Count > 0
                        ? "แก้ไขข้อมูลคำขอ: " + string.Join(", ", changes)
                        : "กดบันทึกข้อมูลคำขอ (ไม่มีการเปลี่ยนแปลงค่า)";

                    await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                    var updateQuery =
                        from _data in _bcg.RentalRequests
                        where _data.Active == 1
                            && _data.Id == id
                            && _data.RequesterId == requesterId.Value
                            && (_data.StatusId == DataFactory.StatusDraft || _data.StatusId == DataFactory.StatusRevisionRequired)
                            && _data.ConcurrencyVersion == expectedVersion
                        select _data;

                    var affected = await updateQuery
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

                    // บันทึก Log การแก้ไขร่างเฉพาะเมื่อมีการเปลี่ยนแปลงข้อมูลจริง (หรือบันทึกพร้อมรายละเอียดจากอะไรเป็นอะไร)
                    if (changes.Count > 0)
                    {
                        _bcg.Logs.Add(new Log
                        {
                            RequestId = id,
                            ActorId = requesterId.Value,
                            Action = DataFactory.LogAction.LogActionEdit,
                            FromStatusId = existingRequest.StatusId,
                            ToStatusId = existingRequest.StatusId,
                            RequestRevisionId = null,
                            ActionDate = now,
                            Details = editLogDetails,
                            TraceId = HttpContext.TraceIdentifier,
                            Active = 1,
                            CreateBy = requesterId.Value,
                            CreateDate = now
                        });
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
                var innerMsg = dbEx.InnerException?.Message ?? dbEx.Message;
                if (innerMsg.Contains("UX_RentalRequests_ContractPeriod", StringComparison.OrdinalIgnoreCase))
                {
                    return Conflict(new
                    {
                        detail = "ไม่สามารถบันทึกได้เนื่องจากสัญญาและช่วงวันที่ซ้ำกับรายการอื่นในระบบ"
                    });
                }

                return BadRequest(new
                {
                    detail = "ข้อมูลไม่ผ่านเงื่อนไขของฐานข้อมูล: " + innerMsg
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
                var requestQuery =
                    from _data in _bcg.RentalRequests.Include(x => x.Requester).AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == id
                        && _data.RequesterId == requesterId.Value
                    select _data;

                var rentalRequest = await requestQuery.SingleOrDefaultAsync(cancellationToken);

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
                    return BadRequest(new { detail = "กรุณาเลือกแผนก/ฝ่าย (Cost Center) ก่อนส่งอนุมัติ" });
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
                var contractQuery =
                    from _data in _bcg.MasterContracts.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == rentalRequest.ContractId.Value
                        && _data.Vendor.Active == 1
                        && _data.RentalType.Active == 1
                    select new
                    {
                        _data.Id,
                        _data.ContractNumber,
                        _data.VendorId,
                        VendorCode = _data.Vendor.VendorCode,
                        VendorName = _data.Vendor.Name,
                        _data.RentalTypeId,
                        RentalTypeCode = _data.RentalType.RentalTypeCode,
                        RentalTypeName = _data.RentalType.Name
                    };

                var contractInfo = await contractQuery.SingleOrDefaultAsync(cancellationToken);

                if (contractInfo == null)
                {
                    return BadRequest(new
                    {
                        detail = "สัญญา ผู้ให้เช่า หรือประเภทค่าเช่าที่เลือกไม่พร้อมใช้งานในระบบ"
                    });
                }

                // ตรวจสอบข้อมูล Master CostCenter
                var costCenterQuery =
                    from _data in _bcg.MasterCostCenters.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == rentalRequest.CostCenterId.Value
                    select new
                    {
                        _data.Id,
                        _data.CostCenterCode,
                        _data.Name
                    };

                var costCenterInfo = await costCenterQuery.SingleOrDefaultAsync(cancellationToken);

                if (costCenterInfo == null)
                {
                    return BadRequest(new { detail = "แผนก/ฝ่าย (Cost Center) ที่เลือกไม่พร้อมใช้งาน" });
                }

                // 4. ตรวจคำขอซ้ำ (ตัด RequestId ปัจจุบันออก)
                var duplicateQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id != id
                        && _data.ContractId == contractInfo.Id
                        && _data.PeriodStartDate == periodStart
                        && _data.PeriodEndDate == periodEnd
                        && _data.StatusId != DataFactory.StatusRejected
                        && _data.StatusId != DataFactory.StatusCancelled
                    select _data.Id;

                var hasDuplicate = await duplicateQuery.AnyAsync(cancellationToken);

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
                    var draftAttachmentsQuery =
                        from _data in _bcg.Attachments.AsNoTracking()
                        where _data.Active == 1
                            && _data.RequestId == id
                            && _data.RequestRevisionId == null
                        orderby _data.Id
                        select _data.FileName;

                    activeDraftFiles = await draftAttachmentsQuery.ToListAsync(cancellationToken);
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

                var assignmentsQuery =
                    from _data in _bcg.ApprovalAssignments.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequesterId == requesterId.Value
                    select new
                    {
                        _data.ApprovalRoleId,
                        _data.ApproverId,
                        ApproverUserName = _data.Approver.UserName,
                        ApproverDisplayName = _data.Approver.DisplayName,
                        ApproverActive = _data.Approver.Active,
                        ApproverCanLogin = _data.Approver.CanLogin,
                        ApproverRoleId = _data.Approver.RoleId,
                        ApproverRoleActive = _data.Approver.Role.Active,
                        ApproverRoleCode = _data.Approver.Role.RoleCode
                    };

                var assignments = await assignmentsQuery.ToListAsync(cancellationToken);

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
                var now = DateTime.Now;
                var nextRevisionNumber = (rentalRequest.CurrentRevision ?? 0) + 1;
                var isResubmit = rentalRequest.StatusId == DataFactory.StatusRevisionRequired;

                // หากเป็นการส่งขออนุมัติใหม่หลังจากถูกส่งคืนให้แก้ไข (REVISION_REQUIRED)
                // ต้องตรวจสอบว่าผู้ขอได้ทำการแก้ไขข้อมูลหรือเอกสารแนบแตกต่างจาก Revision ล่าสุดที่ถูกส่งคืนหรือไม่
                string? resubmitDiffSummary = null;
                if (isResubmit && rentalRequest.CurrentRevision.HasValue)
                {
                    var lastRevQuery =
                        from _data in _bcg.RequestRevisions.AsNoTracking()
                        where _data.Active == 1
                            && _data.RequestId == id
                            && _data.RevisionNumber == rentalRequest.CurrentRevision.Value
                        select _data;

                    var lastRev = await lastRevQuery.SingleOrDefaultAsync(cancellationToken);
                    if (lastRev != null)
                    {
                        var lastRevFilesQuery =
                            from _data in _bcg.Attachments.AsNoTracking()
                            where _data.Active == 1
                                && _data.RequestId == id
                                && _data.RequestRevisionId == lastRev.Id
                            orderby _data.Id
                            select _data.FileName;

                        var lastRevFiles = await lastRevFilesQuery.ToListAsync(cancellationToken);

                        string? lastNotes = null;
                        try
                        {
                            using var snapDoc = JsonDocument.Parse(lastRev.SnapshotJson);
                            if (snapDoc.RootElement.TryGetProperty("Notes", out var notesEl) && notesEl.ValueKind == JsonValueKind.String)
                            {
                                lastNotes = notesEl.GetString();
                            }
                        }
                        catch
                        {
                            // Ignore JSON parse error
                        }

                        var revDiffs = new List<string>();

                        if (lastRev.ContractId != contractInfo.Id)
                        {
                            revDiffs.Add($"เลขที่สัญญา (เป็น '{contractInfo.ContractNumber}')");
                        }
                        if (lastRev.CostCenterId != costCenterInfo.Id)
                        {
                            revDiffs.Add($"แผนก/ฝ่าย (เป็น '{costCenterInfo.CostCenterCode}')");
                        }
                        if (lastRev.PeriodStartDate.Date != periodStart)
                        {
                            revDiffs.Add($"วันเริ่มต้นงวด (จาก '{lastRev.PeriodStartDate:dd/MM/yyyy}' เป็น '{periodStart:dd/MM/yyyy}')");
                        }
                        if (lastRev.PeriodEndDate.Date != periodEnd)
                        {
                            revDiffs.Add($"วันสิ้นสุดงวด (จาก '{lastRev.PeriodEndDate:dd/MM/yyyy}' เป็น '{periodEnd:dd/MM/yyyy}')");
                        }
                        if (lastRev.Amount != amount)
                        {
                            revDiffs.Add($"ยอดเงินก่อน VAT (จาก '{lastRev.Amount:N2}' เป็น '{amount:N2}' บาท)");
                        }
                        if (lastRev.VatRate != vatRate)
                        {
                            revDiffs.Add($"อัตรา VAT (จาก '{lastRev.VatRate:N2}%' เป็น '{vatRate:N2}%')");
                        }

                        var oldRevNotesNorm = string.IsNullOrWhiteSpace(lastNotes) ? "-" : lastNotes.Trim();
                        var currentNotesNorm = string.IsNullOrWhiteSpace(rentalRequest.Notes) ? "-" : rentalRequest.Notes.Trim();
                        if (!string.Equals(oldRevNotesNorm, currentNotesNorm, StringComparison.Ordinal))
                        {
                            revDiffs.Add($"หมายเหตุ (จาก '{oldRevNotesNorm}' เป็น '{currentNotesNorm}')");
                        }

                        var oldRevFilesJoined = lastRevFiles.Count > 0 ? string.Join(", ", lastRevFiles) : "ไม่มีไฟล์";
                        var currentFilesJoined = activeDraftFiles.Count > 0 ? string.Join(", ", activeDraftFiles) : "ไม่มีไฟล์";
                        if (!string.Equals(oldRevFilesJoined, currentFilesJoined, StringComparison.OrdinalIgnoreCase))
                        {
                            revDiffs.Add($"ไฟล์เอกสารแนบ (จาก '{oldRevFilesJoined}' เป็น '{currentFilesJoined}')");
                        }

                        if (revDiffs.Count == 0)
                        {
                            return BadRequest(new
                            {
                                detail = "คำขอนี้ถูกส่งคืนให้แก้ไข กรุณาแก้ไขข้อมูลหรืออัปเดตเอกสารแนบอย่างน้อย 1 รายการก่อนส่งขออนุมัติใหม่"
                            });
                        }

                        resubmitDiffSummary = string.Join(", ", revDiffs);
                    }
                }

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

                // ยกเลิก Step เก่าของ Revision ก่อนหน้าที่อาจค้างอยู่ให้เป็น Superseded (ต้องกำหนด ActionDate ตาม CK_ApprovalSteps_ActionTime)
                var supersedeStepQuery =
                    from _data in _bcg.ApprovalSteps
                    where _data.Active == 1
                        && _data.RequestId == id
                        && (_data.Status == DataFactory.StepStatus.StepStatusPending ||
                            _data.Status == DataFactory.StepStatus.StepStatusWaiting)
                    select _data;

                await supersedeStepQuery
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(s => s.Status, DataFactory.StepStatus.StepStatusSuperseded)
                        .SetProperty(s => s.ActionDate, s => s.ActionDate ?? now)
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
                var submitUpdateQuery =
                    from _data in _bcg.RentalRequests
                    where _data.Active == 1
                        && _data.Id == id
                        && _data.RequesterId == requesterId.Value
                        && (_data.StatusId == DataFactory.StatusDraft || _data.StatusId == DataFactory.StatusRevisionRequired)
                        && _data.ConcurrencyVersion == expectedVersion
                    select _data;

                var affected = await submitUpdateQuery
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

                // บันทึก Audit Log (ต้องตรงกับ CK_Logs_Action = 'Submit' หรือ 'Resubmit')
                _bcg.Logs.Add(new Log
                {
                    RequestId = id,
                    ActorId = requesterId.Value,
                    Action = isResubmit
                        ? DataFactory.LogAction.LogActionResubmit
                        : DataFactory.LogAction.LogActionSubmit,
                    FromStatusId = rentalRequest.StatusId,
                    ToStatusId = DataFactory.StatusPendingManager,
                    RequestRevisionId = newRevision.Id,
                    ActionDate = now,
                    Details = isResubmit
                        ? $"ส่งคำขออนุมัติใหม่ (รอบที่ #{nextRevisionNumber}) | รายการที่แก้ไข: {resubmitDiffSummary}"
                        : $"ส่งคำขออนุมัติครั้งแรก (รอบที่ #{nextRevisionNumber})",
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
                var innerMsg = dbEx.InnerException?.Message ?? dbEx.Message;
                if (innerMsg.Contains("UX_RentalRequests_ContractPeriod", StringComparison.OrdinalIgnoreCase))
                {
                    return Conflict(new
                    {
                        detail = "ไม่สามารถส่งอนุมัติได้เนื่องจากสัญญาและงวดวันที่ซ้ำกับรายการอื่นในระบบ"
                    });
                }

                return BadRequest(new
                {
                    detail = "ข้อมูลไม่ผ่านเงื่อนไขของฐานข้อมูล: " + innerMsg
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

                var requestQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == id
                        && _data.RequesterId == requesterId.Value
                    select new
                    {
                        _data.Id,
                        _data.StatusId,
                        _data.CurrentRevision,
                        _data.ConcurrencyVersion
                    };

                var rentalRequest = await requestQuery.SingleOrDefaultAsync(cancellationToken);

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
                    var revisionQuery =
                        from _data in _bcg.RequestRevisions.AsNoTracking()
                        where _data.Active == 1
                            && _data.RequestId == id
                            && _data.RevisionNumber == rentalRequest.CurrentRevision.Value
                        select (int?)_data.Id;

                    currentRevisionId = await revisionQuery.SingleOrDefaultAsync(cancellationToken);
                }

                var now = DateTime.Now;
                await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

                var cancelReqQuery =
                    from _data in _bcg.RentalRequests
                    where _data.Active == 1
                        && _data.Id == id
                        && _data.RequesterId == requesterId.Value
                        && (_data.StatusId == DataFactory.StatusDraft ||
                            _data.StatusId == DataFactory.StatusPendingManager ||
                            _data.StatusId == DataFactory.StatusPendingExecutive ||
                            _data.StatusId == DataFactory.StatusRevisionRequired)
                        && _data.ConcurrencyVersion == expectedVersion
                    select _data;

                var affected = await cancelReqQuery
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
                var cancelStepQuery =
                    from _data in _bcg.ApprovalSteps
                    where _data.Active == 1
                        && _data.RequestId == id
                        && (_data.Status == DataFactory.StepStatus.StepStatusPending ||
                            _data.Status == DataFactory.StepStatus.StepStatusWaiting)
                    select _data;

                await cancelStepQuery
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
                    Action = DataFactory.LogAction.LogActionCancel,
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
                var accessQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where _data.Active == 1
                        && _data.Id == id
                        && (isAdmin
                            || _data.RequesterId == currentUserId.Value
                            || _data.ApprovalSteps.Any(s => s.ApproverId == currentUserId.Value && s.Active == 1))
                    select _data.Id;

                var canAccess = await accessQuery.AnyAsync(cancellationToken);

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
            var oldAttachmentsQuery =
                from _data in _bcg.Attachments
                where _data.Active == 1
                    && _data.RequestId == requestId
                    && _data.RequestRevisionId == null
                select _data;

            await oldAttachmentsQuery
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
            var headerQuery =
                from _data in _bcg.RentalRequests.AsNoTracking()
                where _data.Active == 1
                    && _data.Id == id
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
                };

            var header = await headerQuery.SingleOrDefaultAsync(cancellationToken);

            if (header == null)
            {
                return null;
            }

            // ดึงไฟล์แนบ: หากอยู่ใน DRAFT หรือ REVISION_REQUIRED ให้แสดงรายการร่างล่าสุด มิฉะนั้นแสดงตาม Revision ปัจจุบัน
            int? activeRevisionId = null;
            if (header.CurrentRevision.HasValue)
            {
                var revisionIdQuery =
                    from _data in _bcg.RequestRevisions.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RevisionNumber == header.CurrentRevision.Value
                    select (int?)_data.Id;

                activeRevisionId = await revisionIdQuery.SingleOrDefaultAsync(cancellationToken);
            }

            if (header.StatusId == DataFactory.StatusDraft ||
                header.StatusId == DataFactory.StatusRevisionRequired ||
                !activeRevisionId.HasValue)
            {
                var draftFilesQuery =
                    from _data in _bcg.Attachments.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RequestRevisionId == null
                    orderby _data.Id
                    select _data.FileName;

                header.AttachmentFileNames = await draftFilesQuery.ToListAsync(cancellationToken);
            }
            else
            {
                var revFilesQuery =
                    from _data in _bcg.Attachments.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RequestRevisionId == activeRevisionId.Value
                    orderby _data.Id
                    select _data.FileName;

                header.AttachmentFileNames = await revFilesQuery.ToListAsync(cancellationToken);
            }

            // ดึงลำดับขั้นการอนุมัติของ Revision ล่าสุด (ถ้ามี)
            if (activeRevisionId.HasValue)
            {
                var stepsQuery =
                    from _data in _bcg.ApprovalSteps.AsNoTracking()
                    where _data.Active == 1
                        && _data.RequestId == id
                        && _data.RequestRevisionId == activeRevisionId.Value
                    orderby _data.StepNumber
                    select new ApprovalStepItemModel
                    {
                        StepNumber = _data.StepNumber,
                        ApprovalRoleId = _data.ApprovalRoleId,
                        RoleCode = _data.ApprovalRole.RoleCode,
                        RoleName = _data.ApprovalRole.Name,
                        ApproverId = _data.ApproverId,
                        ApproverName = _data.Approver.DisplayName,
                        Status = _data.Status,
                        ActionDate = _data.ActionDate,
                        Reason = _data.Reason
                    };

                header.ApprovalSteps = await stepsQuery.ToListAsync(cancellationToken);
            }

            // ดึงประวัติ Timeline (Logs)
            var logsQuery =
                from _data in _bcg.Logs.AsNoTracking()
                where _data.Active == 1
                    && _data.RequestId == id
                orderby _data.ActionDate descending, _data.Id descending
                select new RequestLogItemModel
                {
                    Id = _data.Id,
                    Action = _data.Action,
                    ActorId = _data.ActorId,
                    ActorName = _data.Actor.DisplayName,
                    FromStatusCode = _data.FromStatus != null ? _data.FromStatus.StatusCode : null,
                    ToStatusCode = _data.ToStatus.StatusCode,
                    RequestRevisionId = _data.RequestRevisionId,
                    ActionDate = _data.ActionDate,
                    Reason = _data.Reason,
                    Details = _data.Details
                };

            header.Logs = await logsQuery.ToListAsync(cancellationToken);

            return header;
        }
    }
}
