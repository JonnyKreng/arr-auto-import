namespace ArrImportSolver.Lidarr.Models;

public sealed class PagingResource<T>
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public string? SortKey { get; set; }
    public string? SortDirection { get; set; }
    public int TotalRecords { get; set; }
    public List<T> Records { get; set; } = new();
}