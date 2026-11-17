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

        public RentalRequestsController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser, ILogger<RentalRequestsController> logger)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
            _logger = logger;
        }

        [HttpPost]
        public async Task<ActionResult<RentalRequestResponseModel>> CreateDraft([FromBody] CreateRentalRequestModel request, CancellationToken cancellationToken)
        {
            try
            {
                var requesterId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (requesterId == null)
                {
                    return Unauthorized();
                }
                int id = request.Id;

                // เช็คยอดเงินที่จะมาเบิก
                if (request.Amount <= 0)
                {
                    return BadRequest(new
                    {
                        detail = "Amount ต้องมากกว่า 0 ไม่เกินขนาดที่รองรับ"
                    });
                }

                // เช็ควันที่ขอ โดยวันสิ้นสุดต้องไม่น้อยกว่าวันเริ่มเช่า
                if (request.PeriodStartDate is DateTime startDate &&
                    request.PeriodEndDate is DateTime endDate &&
                    endDate < startDate)
                {
                    return BadRequest(new
                    {
                        detail = "วันสิ้นสุดต้องไม่ก่อนวันเริ่มต้น"
                    });
                }

                // เช็คว่ามีฝ่ายอยู่จริงไหม
                if (request.CostCenterId is int costCenterId) 
                {
                    var queryCostCenter =
                   from c in _bcg.MasterCostCenters.AsNoTracking()
                   where c.Id == costCenterId && c.Active == 1
                   select c.Id;

                    var exists = await queryCostCenter
                        .AnyAsync(cancellationToken);

                    if (!exists)
                    {
                        return BadRequest(new
                        {
                            detail = "แผนก ไม่พร้อมใช้งาน"
                        });
                    }
                }

                // Vendor ID ต้องอ่านผู้ให้เช่าจากสัญญาที่เลือก
                int? vendorId = null;

                if (request.ContractId is int contractId)
                {
                    var query =
                        from c in _bcg.MasterContracts.AsNoTracking()
                        where c.Id == contractId
                            && c.Active == 1
                            && c.Vendor.Active == 1
                            && c.RentalType.Active == 1
                        select (int?)c.VendorId;

                    vendorId = await query.SingleOrDefaultAsync(
                        cancellationToken);

                    if (vendorId == null)
                    {
                        return BadRequest(new
                        {
                            detail = "สัญญาหรือข้อมูลที่เกี่ยวข้องไม่พร้อมใช้งาน"
                        });
                    }
                }
                // create new
                if (id == 0)
                {
                    var RentalRequestObject = new RentalRequest
                    {
                        RequesterId = requesterId.Value,
                        ContractId = request.ContractId,
                        VendorId = vendorId,
                        CostCenterId = request.CostCenterId,

                        PeriodStartDate = request.PeriodStartDate,
                        PeriodEndDate = request.PeriodEndDate,

                        Amount = request.Amount,
                        VatRate = request.VatRate,
                        Notes = request.Notes,

                        StatusId = DataFactory.StatusDraft,
                        SyncStatus = "NotQueued",
                        ConcurrencyVersion = 1,
                        Active = 1,
                        CreateBy = requesterId.Value,
                        CreateDate = DateTime.Now
                    };

                    _bcg.RentalRequests.Add(RentalRequestObject);
                    await _bcg.SaveChangesAsync(cancellationToken);

                    var queryDetail = DetailQuery(RentalRequestObject.Id, requesterId.Value);
                    var response = await queryDetail.SingleAsync(cancellationToken);

                    return CreatedAtAction(nameof(GetById), new { id = RentalRequestObject.Id }, response);
                }
                // update
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
                    // ตรวจว่ามีรายการของผู้ใช้คนนี้ก่อน
                    var queryCheck =
                        from _data in _bcg.RentalRequests.AsNoTracking()
                        where _data.Id == id
                            && _data.RequesterId == requesterId.Value
                            && _data.Active == 1
                        select _data.Id;
                    var checker = await queryCheck.AnyAsync(cancellationToken);
                    if (!checker)
                    {
                        return NotFound();
                    }
                    var queryUpdate =
                        from _data in _bcg.RentalRequests.AsNoTracking()
                        where _data.Id == id
                            && _data.RequesterId == requesterId.Value
                            && _data.Active == 1
                            && _data.StatusId == DataFactory.StatusDraft
                            && _data.ConcurrencyVersion == expectedVersion
                        select _data;

                    var affected = await queryUpdate.ExecuteUpdateAsync(
                            setters => setters
                            .SetProperty(r => r.ContractId, request.ContractId)
                            .SetProperty(r => r.VendorId, vendorId)
                            .SetProperty(r => r.CostCenterId, request.CostCenterId)
                            .SetProperty(r => r.PeriodStartDate, request.PeriodStartDate)
                            .SetProperty(r => r.PeriodEndDate, request.PeriodEndDate)
                            .SetProperty(r => r.Amount, request.Amount)
                            .SetProperty(r => r.VatRate, request.VatRate)
                            .SetProperty(r => r.Notes, request.Notes)
                            .SetProperty(r => r.ModifyBy, (int?)requesterId.Value)
                            .SetProperty(r => r.ModifyDate, (DateTime?)DateTime.Now)
                            .SetProperty(
                            r => r.ConcurrencyVersion,
                            r => r.ConcurrencyVersion + 1),
                            cancellationToken);

                    if (affected == 0)
                    {
                        return Conflict(new
                        {
                            detail = "สถานะไม่อนุญาตให้แก้ไข กรุณาโหลดข้อมูลใหม่"
                        });
                    }

                    // อ่านข้อมูลพร้อมยอดคำนวณและเวอร์ชันล่าสุด
                    var queryDetail = DetailQuery(id, requesterId.Value);

                    var response = await queryDetail.SingleOrDefaultAsync(
                        cancellationToken);

                    return Ok(response);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "เกิดข้อผิดพลาดขณะสร้าง Draft");

                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถดำเนินการได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
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
                var requesterId = await _getCurrentUser.GetCurrentUserId(
               cancellationToken);

                if (requesterId == null)
                {
                    return Unauthorized();
                }

                var data = DetailQuery(id, requesterId.Value);
                var result = await data.SingleOrDefaultAsync(cancellationToken);

                if (result == null)
                {
                    return NotFound();
                }
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "ไม่สามารถอ่าน RentalRequest {RequestId}",
                    id);

                return StatusCode(500, new
                {
                    title = "ระบบขัดข้อง",
                    status = 500,
                    detail = "ไม่สามารถดำเนินการได้ในขณะนี้ กรุณาลองใหม่ภายหลัง"
                });
            }
        }

        // Query สำหรับอ่านรายละเอียด
        private IQueryable<RentalRequestResponseModel> DetailQuery(
            int id,
            int requesterId)
        {
            var query =
                from _data in _bcg.RentalRequests.AsNoTracking()
                where _data.Id == id
                    && _data.RequesterId == requesterId
                    && _data.Active == 1
                select new RentalRequestResponseModel
                {
                    Id = _data.Id,
                    RequesterId = _data.RequesterId,
                    ContractId = _data.ContractId,
                    VendorId = _data.VendorId,
                    CostCenterId = _data.CostCenterId,
                    PeriodStartDate = _data.PeriodStartDate,
                    PeriodEndDate = _data.PeriodEndDate,
                    Amount = _data.Amount,
                    VatRate = _data.VatRate,
                    VatAmount = _data.VatAmount,
                    TotalAmount = _data.TotalAmount,
                    Notes = _data.Notes,
                    StatusCode = _data.Status.StatusCode,
                    ConcurrencyVersion = _data.ConcurrencyVersion,
                    CreateDate = _data.CreateDate
                };
            return query;
        }
    }
}
