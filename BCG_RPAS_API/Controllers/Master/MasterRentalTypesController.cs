using BCG_RPAS_API.DTOs.Master;

namespace BCG_RPAS_API.Controllers.Master
{
    [ApiController]
    [Authorize]
    [Tags("Master")]
    [Route("api/master/rental-type")]
    public class MasterRentalTypesController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public MasterRentalTypesController(BCG_RPASContext bcg, GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> Get(
            [FromQuery] bool includeInactive = false,
            CancellationToken cancellationToken = default)
        {
            var query = from _data in _bcg.MasterRentalTypes.AsNoTracking()
                        where includeInactive || _data.Active == 1
                        orderby _data.Id
                        select new
                        {
                            _data.Id,
                            _data.RentalTypeCode,
                            _data.Name,
                            _data.Active,
                            _data.ConcurrencyVersion,
                            _data.ModifyDate,
                            _data.CreateDate
                        };

            var result = await query.ToListAsync(cancellationToken);
            return Ok(result);
        }

        [HttpPost]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertRentalTypeModel request,
            CancellationToken cancellationToken)
        {
            var userId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (userId == null) return Unauthorized();

            var code = request.RentalTypeCode?.Trim();
            var name = request.Name?.Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                return BadRequest(new { detail = "กรุณาระบุรหัสประเภทการเช่า (Rental Type Code)" });
            }

            if (code.Length > 100)
            {
                return BadRequest(new { detail = "รหัสประเภทการเช่าต้องไม่เกิน 100 ตัวอักษร" });
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { detail = "กรุณาระบุชื่อประเภทการเช่า" });
            }

            if (name.Length > 200)
            {
                return BadRequest(new { detail = "ชื่อประเภทการเช่าต้องไม่เกิน 200 ตัวอักษร" });
            }

            var isDuplicate = await _bcg.MasterRentalTypes
                .AsNoTracking()
                .AnyAsync(rt => rt.Id != request.Id && rt.RentalTypeCode == code, cancellationToken);
            if (isDuplicate)
            {
                return Conflict(new { detail = $"รหัสประเภทการเช่า '{code}' มีอยู่ในระบบแล้ว" });
            }

            var now = DateTime.Now;
            var activeFlag = request.Active == 0 ? 0 : 1;

            if (request.Id == 0)
            {
                // MasterRentalType has ValueGeneratedNever on Id
                var maxId = await _bcg.MasterRentalTypes
                    .AsNoTracking()
                    .Select(rt => (int?)rt.Id)
                    .MaxAsync(cancellationToken) ?? 0;

                var entity = new MasterRentalType
                {
                    Id = maxId + 1,
                    RentalTypeCode = code,
                    Name = name,
                    Active = activeFlag,
                    ConcurrencyVersion = 1,
                    CreateBy = userId.Value,
                    CreateDate = now
                };

                _bcg.MasterRentalTypes.Add(entity);
                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = entity.Id,
                    message = "เพิ่มข้อมูลประเภทการเช่าเรียบร้อยแล้ว"
                });
            }
            else
            {
                var existing = await _bcg.MasterRentalTypes
                    .SingleOrDefaultAsync(rt => rt.Id == request.Id, cancellationToken);

                if (existing == null)
                {
                    return NotFound(new { detail = "ไม่พบข้อมูลประเภทการเช่าที่ต้องการแก้ไข" });
                }

                if (request.ExpectedConcurrencyVersion.HasValue &&
                    existing.ConcurrencyVersion != request.ExpectedConcurrencyVersion.Value)
                {
                    return Conflict(new { detail = "ข้อมูลถูกแก้ไขโดยผู้ใช้อื่น กรุณาโหลดข้อมูลใหม่" });
                }

                existing.RentalTypeCode = code;
                existing.Name = name;
                existing.Active = activeFlag;
                existing.ModifyBy = userId.Value;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;

                await _bcg.SaveChangesAsync(cancellationToken);

                return Ok(new
                {
                    id = existing.Id,
                    message = "บันทึกการแก้ไขข้อมูลประเภทการเช่าเรียบร้อยแล้ว"
                });
            }
        }
    }
}
