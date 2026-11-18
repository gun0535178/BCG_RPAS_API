using BCG_RPAS_API.DTOs.Approvals;
using Microsoft.AspNetCore.Mvc;

namespace BCG_RPAS_API.Controllers
{
    [ApiController]
    [Authorize(Roles = "ADMINISTRATOR")]
    [Route("api/approval-assignments")]
    public class ApprovalAssignmentsController : ControllerBase
    {
        private readonly BCG_RPASContext _bcg;
        private readonly GetCurrentUser _getCurrentUser;

        public ApprovalAssignmentsController(
            BCG_RPASContext bcg,
            GetCurrentUser getCurrentUser)
        {
            _bcg = bcg;
            _getCurrentUser = getCurrentUser;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
        {
            // 1. ดึงรายชื่อผู้ใช้งานทั้งหมดที่ Active == 1 และไม่ใช่ SYSTEM
            var usersQuery =
                from _data in _bcg.Users.AsNoTracking()
                where _data.Active == 1
                    && _data.CanLogin == 1
                    && _data.Role.Active == 1
                    && _data.Role.RoleCode != DataFactory.RoleCode.RoleCodeSystem
                orderby _data.Id
                select new
                {
                    _data.Id,
                    _data.UserName,
                    _data.DisplayName,
                    _data.Email,
                    _data.RoleId,
                    RoleCode = _data.Role.RoleCode,
                    RoleName = _data.Role.Name
                };

            var users = await usersQuery.ToListAsync(cancellationToken);

            // 2. ดึงรายการสายอนุมัติที่ Active == 1
            var assignmentsQuery =
                from _data in _bcg.ApprovalAssignments.AsNoTracking()
                where _data.Active == 1
                select new
                {
                    _data.Id,
                    _data.RequesterId,
                    _data.ApprovalRoleId,
                    _data.ApproverId,
                    ApproverUserName = _data.Approver.UserName,
                    ApproverDisplayName = _data.Approver.DisplayName,
                    _data.ModifyDate,
                    _data.CreateDate
                };

            var assignments = await assignmentsQuery.ToListAsync(cancellationToken);

            // แยกกลุ่มผู้ขอ (เฉพาะบทบาท REQUESTER เท่านั้นตามหลักการแบ่งแยกหน้าที่)
            var requesters = users
                .Where(u => u.RoleId == DataFactory.RoleRequester)
                .Select(u =>
                {
                    var mgr = assignments.FirstOrDefault(a => a.RequesterId == u.Id && a.ApprovalRoleId == DataFactory.RoleManager);
                    var exec = assignments.FirstOrDefault(a => a.RequesterId == u.Id && a.ApprovalRoleId == DataFactory.RoleExecutive);

                    return new
                    {
                        requesterId = u.Id,
                        userName = u.UserName,
                        displayName = u.DisplayName,
                        roleId = u.RoleId,
                        roleCode = u.RoleCode,
                        roleName = u.RoleName,
                        managerAssignmentId = mgr?.Id,
                        managerApproverId = mgr?.ApproverId,
                        managerDisplayName = mgr?.ApproverDisplayName,
                        managerUserName = mgr?.ApproverUserName,
                        executiveAssignmentId = exec?.Id,
                        executiveApproverId = exec?.ApproverId,
                        executiveDisplayName = exec?.ApproverDisplayName,
                        executiveUserName = exec?.ApproverUserName
                    };
                })
                .ToList();

            var managers = users.Where(u => u.RoleId == DataFactory.RoleManager).ToList();
            var executives = users.Where(u => u.RoleId == DataFactory.RoleExecutive).ToList();

            return Ok(new
            {
                requesters,
                managers,
                executives
            });
        }

        [HttpPost]
        public async Task<IActionResult> Upsert(
            [FromBody] UpsertApprovalAssignmentModel request,
            CancellationToken cancellationToken)
        {
            var adminId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (adminId == null) return Unauthorized();

            if (request.RequesterId <= 0)
            {
                return BadRequest(new { detail = "กรุณาเลือกผู้ขออนุมัติ (Requester)" });
            }

            if (request.ManagerApproverId <= 0)
            {
                return BadRequest(new { detail = "กรุณาเลือกหัวหน้าผู้อนุมัติขั้นที่ 1 (Manager)" });
            }

            if (request.ExecutiveApproverId <= 0)
            {
                return BadRequest(new { detail = "กรุณาเลือกผู้บริหารผู้อนุมัติขั้นที่ 2 (Executive)" });
            }

            if (request.ManagerApproverId == request.RequesterId || request.ExecutiveApproverId == request.RequesterId)
            {
                return BadRequest(new { detail = "ผู้ขออนุมัติไม่สามารถเป็นผู้อนุมัติให้ตนเองได้" });
            }

            if (request.ManagerApproverId == request.ExecutiveApproverId)
            {
                return BadRequest(new { detail = "ผู้อนุมัติขั้นที่ 1 (Manager) และขั้นที่ 2 (Executive) ต้องไม่เป็นบุคคลเดียวกัน" });
            }

            // ตรวจสอบว่าผู้ขอมีอยู่จริงและเป็นบทบาท REQUESTER เท่านั้น
            var requesterQuery =
                from _data in _bcg.Users.AsNoTracking()
                where _data.Active == 1
                    && _data.Id == request.RequesterId
                    && _data.RoleId == DataFactory.RoleRequester
                select _data.Id;

            if (!await requesterQuery.AnyAsync(cancellationToken))
            {
                return BadRequest(new { detail = "ผู้ขออนุมัติที่เลือกต้องมีบทบาทเป็น ผู้ขออนุมัติ (REQUESTER) ที่เปิดใช้งานอยู่เท่านั้น" });
            }

            // ตรวจสอบว่า Manager มีบทบาทเป็น MANAGER จริงและ Active
            var managerQuery =
                from _data in _bcg.Users.AsNoTracking()
                where _data.Active == 1
                    && _data.CanLogin == 1
                    && _data.Id == request.ManagerApproverId
                    && _data.RoleId == DataFactory.RoleManager
                select _data.Id;

            if (!await managerQuery.AnyAsync(cancellationToken))
            {
                return BadRequest(new { detail = "ผู้ใช้งานที่เลือกไม่ใช่บทบาทผู้จัดการ (MANAGER) ที่พร้อมใช้งาน" });
            }

            // ตรวจสอบว่า Executive มีบทบาทเป็น EXECUTIVE จริงและ Active
            var executiveQuery =
                from _data in _bcg.Users.AsNoTracking()
                where _data.Active == 1
                    && _data.CanLogin == 1
                    && _data.Id == request.ExecutiveApproverId
                    && _data.RoleId == DataFactory.RoleExecutive
                select _data.Id;

            if (!await executiveQuery.AnyAsync(cancellationToken))
            {
                return BadRequest(new { detail = "ผู้ใช้งานที่เลือกไม่ใช่บทบาทผู้บริหาร (EXECUTIVE) ที่พร้อมใช้งาน" });
            }

            var now = DateTime.Now;
            await using var transaction = await _bcg.Database.BeginTransactionAsync(cancellationToken);

            // Upsert สายอนุมัติขั้นที่ 1 (Manager)
            await UpsertRoleAssignmentAsync(
                request.RequesterId,
                DataFactory.RoleManager,
                request.ManagerApproverId,
                adminId.Value,
                now,
                cancellationToken);

            // Upsert สายอนุมัติขั้นที่ 2 (Executive)
            await UpsertRoleAssignmentAsync(
                request.RequesterId,
                DataFactory.RoleExecutive,
                request.ExecutiveApproverId,
                adminId.Value,
                now,
                cancellationToken);

            await _bcg.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return Ok(new
            {
                requesterId = request.RequesterId,
                message = "บันทึกการตั้งค่าสายอนุมัติเรียบร้อยแล้ว"
            });
        }

        [HttpPost("{requesterId:int}/delete")]
        public async Task<IActionResult> SoftDeleteByRequester(
            int requesterId,
            CancellationToken cancellationToken)
        {
            var adminId = await _getCurrentUser.GetCurrentUserId(cancellationToken);
            if (adminId == null) return Unauthorized();

            var now = DateTime.Now;

            var activeQuery =
                from _data in _bcg.ApprovalAssignments
                where _data.Active == 1
                    && _data.RequesterId == requesterId
                select _data;

            var existingList = await activeQuery.ToListAsync(cancellationToken);

            if (existingList.Count == 0)
            {
                return NotFound(new { detail = "ไม่พบสายอนุมัติที่ต้องการลบ" });
            }

            foreach (var item in existingList)
            {
                item.Active = 0;
                item.ModifyBy = adminId.Value;
                item.ModifyDate = now;
                item.ConcurrencyVersion += 1;
            }

            await _bcg.SaveChangesAsync(cancellationToken);

            return Ok(new
            {
                requesterId,
                message = "ยกเลิกผูกสายอนุมัติของผู้ขอรายนี้เรียบร้อยแล้ว"
            });
        }

        private async Task UpsertRoleAssignmentAsync(
            int requesterId,
            int approvalRoleId,
            int approverId,
            int actorId,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var existingQuery =
                from _data in _bcg.ApprovalAssignments
                where _data.Active == 1
                    && _data.RequesterId == requesterId
                    && _data.ApprovalRoleId == approvalRoleId
                select _data;

            var existing = await existingQuery.SingleOrDefaultAsync(cancellationToken);

            if (existing != null)
            {
                existing.ApproverId = approverId;
                existing.ModifyBy = actorId;
                existing.ModifyDate = now;
                existing.ConcurrencyVersion += 1;
            }
            else
            {
                _bcg.ApprovalAssignments.Add(new ApprovalAssignment
                {
                    RequesterId = requesterId,
                    ApprovalRoleId = approvalRoleId,
                    ApproverId = approverId,
                    Active = 1,
                    ConcurrencyVersion = 1,
                    CreateBy = actorId,
                    CreateDate = now
                });
            }
        }
    }
}
