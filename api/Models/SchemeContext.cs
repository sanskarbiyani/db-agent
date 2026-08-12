namespace DbAgent.Api.Models
{
    public record ColumnInfo(string Name, string DataType, bool IsNullable);
    public record TableInfo(string Name, List<ColumnInfo> Columns);
    public record ConstraintInfo(string TableName, string ColumnName, string ConstraintType);
    public record SchemaContext(List<TableInfo> Tables, List<ConstraintInfo> Constraints);
}
