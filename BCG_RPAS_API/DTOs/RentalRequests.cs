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
        public int? ExpectedConcurrencyVersion { get; set; }
    }

    public class RentalRequestResponseModel
    {
        public int Id { get; set; }

        public int RequesterId { get; set; }

        public int? ContractId { get; set; }

        public int? VendorId { get; set; }

        public int? CostCenterId { get; set; }

        public DateTime? PeriodStartDate { get; set; }

        public DateTime? PeriodEndDate { get; set; }

        public decimal? Amount { get; set; }

        public decimal? VatRate { get; set; }

        public decimal? VatAmount { get; set; }

        public decimal? TotalAmount { get; set; }

        public string? Notes { get; set; }

        public string StatusCode { get; set; } = string.Empty;

        public int ConcurrencyVersion { get; set; }

        public DateTime CreateDate { get; set; }
    }
}
