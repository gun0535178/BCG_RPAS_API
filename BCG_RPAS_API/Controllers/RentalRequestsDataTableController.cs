using BCG_RPAS_API.DTOs.DataTable;
using BCG_RPAS_API.DTOs.RentalRequests;
using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/rental-requests/datatable")]
    public class RentalRequestsDataTableController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;
        private readonly ILogger<RentalRequestsDataTableController> _logger;

        public RentalRequestsDataTableController(
            BCG_RPASContext bcg,
            GetCurrentUser getCurrentUser,
            ILogger<RentalRequestsDataTableController> logger)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
            _logger = logger;
        }

        [HttpPost]
        public async Task<ActionResult<DataTableResponseModel<RentalRequestsDataTable>>> RentalRequestsRequestDataTable(
            [FromBody] DataTableRequestModel request,
            CancellationToken cancellationToken)
        {
            try
            {
                var requesterId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
                if (requesterId == null)
                {
                    return Unauthorized();
                }

                var isAdmin = User.IsInRole(DataFactory.RoleCode.RoleCodeAdministrator);

                // กรองเฉพาะรายการที่ผู้ใช้มีสิทธิ์เห็น (เจ้าของคำขอ, ผู้อนุมัติในสายงาน, หรือ Admin)
                var baseQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where _data.Active == 1
                        && (isAdmin
                            || _data.RequesterId == requesterId.Value
                            || _data.ApprovalSteps.Any(s => s.ApproverId == requesterId.Value && s.Active == 1))
                    select _data;

                var recordsTotal = await baseQuery.CountAsync(cancellationToken);

                var search = request.Search?.Trim() ?? string.Empty;
                var hasSearchId = int.TryParse(search.TrimStart('#'), out var searchId);

                // กรองข้อมูลตามสถานะและคำค้น
                var filteredQuery =
                    from _data in baseQuery
                    where (!request.StatusId.HasValue || _data.StatusId == request.StatusId.Value)
                        && (search == ""
                            || (hasSearchId && _data.Id == searchId)
                            || (_data.MasterContract != null && _data.MasterContract.ContractNumber.Contains(search))
                            || (_data.Vendor != null && (_data.Vendor.Name.Contains(search) || _data.Vendor.VendorCode.Contains(search)))
                            || (_data.CostCenter != null && (_data.CostCenter.CostCenterCode.Contains(search) || _data.CostCenter.Name.Contains(search)))
                            || (_data.Notes != null && _data.Notes.Contains(search))
                            || _data.Status.StatusCode.Contains(search)
                            || _data.Status.Name.Contains(search))
                    select _data;

                var recordsFiltered = await filteredQuery.CountAsync(cancellationToken);

                IOrderedQueryable<RentalRequest> orderedQuery;

                // เรียงคอลัมน์
                switch (request.OrderBy, request.OrderDir)
                {
                    case ("id", "asc"): orderedQuery = from _data in filteredQuery orderby _data.Id select _data; break;
                    case ("id", "desc"): orderedQuery = from _data in filteredQuery orderby _data.Id descending select _data; break;
                    case ("createDate", "asc"): orderedQuery = from _data in filteredQuery orderby _data.CreateDate, _data.Id select _data; break;
                    case ("createDate", "desc"): orderedQuery = from _data in filteredQuery orderby _data.CreateDate descending, _data.Id descending select _data; break;
                    case ("amount", "asc"): orderedQuery = from _data in filteredQuery orderby _data.Amount, _data.Id select _data; break;
                    case ("amount", "desc"): orderedQuery = from _data in filteredQuery orderby _data.Amount descending, _data.Id descending select _data; break;
                    case ("totalAmount", "asc"): orderedQuery = from _data in filteredQuery orderby _data.TotalAmount, _data.Id select _data; break;
                    case ("totalAmount", "desc"): orderedQuery = from _data in filteredQuery orderby _data.TotalAmount descending, _data.Id descending select _data; break;
                    case ("statusCode", "asc"): orderedQuery = from _data in filteredQuery orderby _data.Status.StatusCode, _data.Id select _data; break;
                    case ("statusCode", "desc"): orderedQuery = from _data in filteredQuery orderby _data.Status.StatusCode descending, _data.Id descending select _data; break;
                    default: return BadRequest(new { detail = "OrderBy หรือ OrderDir ไม่ถูกต้อง" });
                }

                // แบ่งหน้าที่ฐานข้อมูล แล้วเลือกเฉพาะฟิลด์ที่ใช้
                var pageQuery =
                    from _data in orderedQuery
                        .Skip(request.Start)
                        .Take(request.Length)
                    select new RentalRequestsDataTable
                    {
                        Id = _data.Id,
                        ContractId = _data.ContractId,
                        ContractNumber = _data.MasterContract != null ? _data.MasterContract.ContractNumber : null,
                        VendorName = _data.Vendor != null ? _data.Vendor.Name : null,
                        PeriodStartDate = _data.PeriodStartDate,
                        PeriodEndDate = _data.PeriodEndDate,
                        Amount = _data.Amount,
                        TotalAmount = _data.TotalAmount,
                        StatusId = _data.StatusId,
                        StatusCode = _data.Status.StatusCode,
                        SyncStatus = _data.SyncStatus,
                        CreateDate = _data.CreateDate,
                        ConcurrencyVersion = _data.ConcurrencyVersion
                    };

                var data = await pageQuery.ToListAsync(cancellationToken);

                return Ok(new DataTableResponseModel<RentalRequestsDataTable>
                {
                    Draw = request.Draw,
                    RecordsTotal = recordsTotal,
                    RecordsFiltered = recordsFiltered,
                    Data = data
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ไม่สามารถอ่าน RentalRequestsDataTable");

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
