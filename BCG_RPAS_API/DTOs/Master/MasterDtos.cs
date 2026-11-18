namespace BCG_RPAS_API.DTOs.Master
{
    public class UpsertContractModel
    {
        public int Id { get; set; }
        public string? ContractNumber { get; set; }
        public int VendorId { get; set; }
        public int RentalTypeId { get; set; }
        public int Active { get; set; } = 1;
        public int? ExpectedConcurrencyVersion { get; set; }
    }

    public class UpsertVendorModel
    {
        public int Id { get; set; }
        public string? VendorCode { get; set; }
        public string? Name { get; set; }
        public int Active { get; set; } = 1;
        public int? ExpectedConcurrencyVersion { get; set; }
    }

    public class UpsertCostCenterModel
    {
        public int Id { get; set; }
        public string? CostCenterCode { get; set; }
        public string? Name { get; set; }
        public int Active { get; set; } = 1;
        public int? ExpectedConcurrencyVersion { get; set; }
    }

    public class UpsertRentalTypeModel
    {
        public int Id { get; set; }
        public string? RentalTypeCode { get; set; }
        public string? Name { get; set; }
        public int Active { get; set; } = 1;
        public int? ExpectedConcurrencyVersion { get; set; }
    }
}
