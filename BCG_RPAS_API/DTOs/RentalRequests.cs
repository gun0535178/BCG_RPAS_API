using System.ComponentModel.DataAnnotations;

namespace BCG_RPAS_API.DTOs.RentalRequests
{
    public class CreateRentalRequestModel
    {
        public int Id { get; set; }

        public int? ContractId { get; set; }

        public int? CostCenterId { get; set; }

        public DateTime? PeriodStartDate { get; set; }

        public DateTime? PeriodEndDate { get; set; }

        public decimal? Amount { get; set; }

        public decimal? VatRate { get; set; }

        [StringLength(2000)]
        public string? Notes { get; set; }

        public List<string>? AttachmentFileNames { get; set; }

        public int? ExpectedConcurrencyVersion { get; set; }
    }

    public class SubmitRentalRequestModel
    {
        [Required(ErrorMessage = "ต้องระบุ ExpectedConcurrencyVersion")]
        public int? ExpectedConcurrencyVersion { get; set; }

        public List<string>? AttachmentFileNames { get; set; }
    }

    public class CancelRentalRequestModel
    {
        [Required(ErrorMessage = "กรุณาระบุเหตุผลการยกเลิก")]
        [StringLength(2000, ErrorMessage = "เหตุผลการยกเลิกต้องไม่เกิน 2000 ตัวอักษร")]
        public string Reason { get; set; } = string.Empty;

        [Required(ErrorMessage = "ต้องระบุ ExpectedConcurrencyVersion")]
        public int? ExpectedConcurrencyVersion { get; set; }
    }

    public class RentalRequestResponseModel
    {
        public int Id { get; set; }

        public int RequesterId { get; set; }

        public string RequesterName { get; set; } = string.Empty;

        public int? ContractId { get; set; }

        public string? ContractNumber { get; set; }

        public int? VendorId { get; set; }

        public string? VendorName { get; set; }

        public int? RentalTypeId { get; set; }

        public string? RentalTypeName { get; set; }

        public int? CostCenterId { get; set; }

        public string? CostCenterCode { get; set; }

        public string? CostCenterName { get; set; }

        public DateTime? PeriodStartDate { get; set; }

        public DateTime? PeriodEndDate { get; set; }

        public decimal? Amount { get; set; }

        public decimal? VatRate { get; set; }

        public decimal? VatAmount { get; set; }

        public decimal? TotalAmount { get; set; }

        public string? Notes { get; set; }

        public string? CancellationReason { get; set; }

        public int StatusId { get; set; }

        public string StatusCode { get; set; } = string.Empty;

        public string StatusName { get; set; } = string.Empty;

        public string SyncStatus { get; set; } = string.Empty;

        public int? CurrentRevision { get; set; }

        public DateTime? SubmittedDate { get; set; }

        public DateTime? ApprovedDate { get; set; }

        public int ConcurrencyVersion { get; set; }

        public DateTime CreateDate { get; set; }

        public List<string> AttachmentFileNames { get; set; } = new();

        public List<ApprovalStepItemModel> ApprovalSteps { get; set; } = new();

        public List<RequestLogItemModel> Logs { get; set; } = new();
    }

    public class ApprovalStepItemModel
    {
        public int StepNumber { get; set; }

        public int ApprovalRoleId { get; set; }

        public string RoleCode { get; set; } = string.Empty;

        public string RoleName { get; set; } = string.Empty;

        public int ApproverId { get; set; }

        public string ApproverName { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public DateTime? ActionDate { get; set; }

        public string? Reason { get; set; }
    }

    public class RequestLogItemModel
    {
        public int Id { get; set; }

        public string Action { get; set; } = string.Empty;

        public int ActorId { get; set; }

        public string ActorName { get; set; } = string.Empty;

        public string? FromStatusCode { get; set; }

        public string ToStatusCode { get; set; } = string.Empty;

        public int? RequestRevisionId { get; set; }

        public DateTime ActionDate { get; set; }

        public string? Reason { get; set; }

        public string? Details { get; set; }
    }
}
