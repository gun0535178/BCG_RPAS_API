using System.ComponentModel.DataAnnotations;

namespace BCG_RPAS_API.DTOs.Approvals
{
    public class ApprovalInboxItemModel
    {
        public int RequestId { get; set; }

        public int StepId { get; set; }

        public int StepNumber { get; set; }

        public int ApprovalRoleId { get; set; }

        public string ApprovalRoleCode { get; set; } = string.Empty;

        public string ApprovalRoleName { get; set; } = string.Empty;

        public int RequesterId { get; set; }

        public string RequesterName { get; set; } = string.Empty;

        public int? ContractId { get; set; }

        public string? ContractNumber { get; set; }

        public string? VendorName { get; set; }

        public string? RentalTypeName { get; set; }

        public string? CostCenterCode { get; set; }

        public string? CostCenterName { get; set; }

        public DateTime? PeriodStartDate { get; set; }

        public DateTime? PeriodEndDate { get; set; }

        public decimal? Amount { get; set; }

        public decimal? VatRate { get; set; }

        public decimal? VatAmount { get; set; }

        public decimal? TotalAmount { get; set; }

        public string? Notes { get; set; }

        public int StatusId { get; set; }

        public string StatusCode { get; set; } = string.Empty;

        public string StatusName { get; set; } = string.Empty;

        public int? CurrentRevision { get; set; }

        public DateTime? SubmittedDate { get; set; }

        public int RequestConcurrencyVersion { get; set; }

        public int StepConcurrencyVersion { get; set; }
    }

    public class ApproveRequestModel
    {
        [Required(ErrorMessage = "ต้องระบุ ExpectedConcurrencyVersion")]
        public int? ExpectedConcurrencyVersion { get; set; }

        [StringLength(2000, ErrorMessage = "หมายเหตุต้องไม่เกิน 2000 ตัวอักษร")]
        public string? Reason { get; set; }
    }

    public class DecisionWithReasonModel
    {
        [Required(ErrorMessage = "กรุณาระบุเหตุผล")]
        [StringLength(2000, ErrorMessage = "เหตุผลต้องไม่เกิน 2000 ตัวอักษร")]
        public string Reason { get; set; } = string.Empty;

        [Required(ErrorMessage = "ต้องระบุ ExpectedConcurrencyVersion")]
        public int? ExpectedConcurrencyVersion { get; set; }
    }
}
