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
        public async Task<ActionResult<DataTableResponseModel<RentalRequestsDataTable>>> RentalRequestsRequestDataTable([FromBody] DataTableRequestModel request, CancellationToken cancellationToken)
        {
            try
            {
                var requesterId = await _getCurrentUser.GetCurrentUserId(
                cancellationToken);

                if (requesterId == null)
                {
                    return Unauthorized();
                }

                var baseQuery =
                    from _data in _bcg.RentalRequests.AsNoTracking()
                    where  _data.Active == 1
                    select _data;

                var recordsTotal = await baseQuery.CountAsync(cancellationToken);

                // ข้อมูลใช้ search
                var search = request.Search?.Trim() ?? string.Empty;

                // กรองข้อมูลตามสถานะและคำค้น
                var filteredQuery =
                    from r in baseQuery
                    where (!request.StatusId.HasValue ||
                           r.StatusId == request.StatusId.Value)
                        && (search == ""
                            || (r.Notes != null && r.Notes.Contains(search))
                            || r.Status.StatusCode.Contains(search))
                    select r;

                var recordsFiltered = await filteredQuery.CountAsync(
                    cancellationToken);

                IOrderedQueryable<RentalRequest> orderedQuery;

                // เรียงคอลัมน์
                switch (request.OrderBy, request.OrderDir)
                {
                    case ("id", "asc"): orderedQuery = from r in filteredQuery orderby r.Id select r; break;
                    case ("id", "desc"): orderedQuery = from r in filteredQuery orderby r.Id descending select r; break;
                    case ("createDate", "asc"): orderedQuery = from r in filteredQuery orderby r.CreateDate, r.Id select r; break;
                    case ("createDate", "desc"): orderedQuery = from r in filteredQuery orderby r.CreateDate descending, r.Id descending select r; break;
                    case ("amount", "asc"): orderedQuery = from r in filteredQuery orderby r.Amount, r.Id select r; break;
                    case ("amount", "desc"): orderedQuery = from r in filteredQuery orderby r.Amount descending, r.Id descending select r; break;
                    case ("totalAmount", "asc"): orderedQuery = from r in filteredQuery orderby r.TotalAmount, r.Id select r; break;
                    case ("totalAmount", "desc"):  orderedQuery = from r in filteredQuery orderby r.TotalAmount descending, r.Id descending select r; break;
                    case ("statusCode", "asc"): orderedQuery = from r in filteredQuery orderby r.Status.StatusCode, r.Id select r; break;
                    case ("statusCode", "desc"): orderedQuery = from r in filteredQuery orderby r.Status.StatusCode descending, r.Id descending select r; break;
                    default: return BadRequest(new { detail = "OrderBy หรือ OrderDir ไม่ถูกต้อง" });
                }

                // แบ่งหน้าที่ฐานข้อมูล แล้วเลือกเฉพาะฟิลด์ที่ใช้
                var pageQuery =
                    from r in orderedQuery
                        .Skip(request.Start)
                        .Take(request.Length)
                    select new RentalRequestsDataTable
                    {
                        Id = r.Id,
                        ContractId = r.ContractId,
                        PeriodStartDate = r.PeriodStartDate,
                        PeriodEndDate = r.PeriodEndDate,
                        Amount = r.Amount,
                        TotalAmount = r.TotalAmount,
                        StatusId = r.StatusId,
                        StatusCode = r.Status.StatusCode,
                        CreateDate = r.CreateDate,
                        ConcurrencyVersion = r.ConcurrencyVersion
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
                _logger.LogError(
                    ex,
                    "ไม่สามารถอ่าน RentalRequestsDataTable");

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
