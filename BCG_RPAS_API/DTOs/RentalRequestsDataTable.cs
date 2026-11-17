using System.ComponentModel.DataAnnotations;

namespace BCG_RPAS_API.DTOs.RentalRequests
{
    public class RentalRequestsDataTable
    {
        public int Id { get; set; }
        public int? ContractId { get; set; }
        public string? ContractNumber { get; set; }
        public string? VendorName { get; set; }
        public DateTime? PeriodStartDate { get; set; }
        public DateTime? PeriodEndDate { get; set; }
        public decimal? Amount { get; set; }
        public decimal? TotalAmount { get; set; }
        public int StatusId { get; set; }
        public string StatusCode { get; set; } = string.Empty;
        public string SyncStatus { get; set; } = string.Empty;
        public DateTime CreateDate { get; set; }
        public int ConcurrencyVersion { get; set; }
    }
}
