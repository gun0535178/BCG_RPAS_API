using System.ComponentModel.DataAnnotations;

namespace BCG_RPAS_API.DTOs.DataTable
{
    public class DataTableRequestModel
    {
        public int Draw { get; set; }
        public int Start { get; set; }
        [Range(1, 100)]
        public int Length { get; set; } = 10;
        [StringLength(100)]
        public string? Search { get; set; }
        [Range(1, int.MaxValue)]
        public int? StatusId { get; set; }
        [Required]
        public string OrderBy { get; set; } = string.Empty;
        [Required]
        [RegularExpression("^(asc|desc)$")]
        public string OrderDir { get; set; } = "desc";
    }
    public class DataTableResponseModel<T>
    {
        public int Draw { get; set; }

        public int RecordsTotal { get; set; }

        public int RecordsFiltered { get; set; }

        public List<T> Data { get; set; } = new();
    }
}
