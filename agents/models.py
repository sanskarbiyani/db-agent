from pydantic import BaseModel


def to_camel(string: str) -> str:
    parts = string.split("_")
    return parts[0] + "".join(word.title() for word in parts[1:])


class CamelModel(BaseModel):
    class Config:
        alias_generator = to_camel
        allow_population_by_field_name = True
        allow_population_by_alias = True


class ColumnInfo(CamelModel):
    """
    ColumnInfo is a Pydantic model that represents information about a database column.
    It includes the column name, data type, and whether it is nullable.
    """

    name: str
    data_type: str
    is_nullable: bool


class TableInfo(CamelModel):
    """
    TableInfo is a Pydantic model that represents information about a database table.
    It includes the table name and a list of columns (ColumnInfo).
    """

    name: str
    columns: list[ColumnInfo]


class ConstraintInfo(CamelModel):
    """
    ConstrainsInfo is a Pydantic model that represents information about a database constraint.
    It includes the table name, column name, and the type of constraint
    """
    table_name: str
    column_name: str
    constraint_type: str


class SchemaContext(CamelModel):
    """
    SchemaContext is a Pydantic model that represents the context of a schema.
    It includes the schema name, version, and any additional metadata.
    """

    tables: list[TableInfo]
    constraints: list[ConstraintInfo]


class GeneratedSQL(BaseModel):
    """
    GeneratedSQL is a Pydantic model that represents the generated SQL statement.
    sql: The generated SQL statement as a string.
    error: An optional string that contains any error message if the SQL generation fails.
    type: An optional string that indicates the type of SQL statement generated (eg. DDL, DML, etc.).
    """

    sql: str
    error: str | None = None


class GenerateSQLRequest(BaseModel):
    """
    GenerateSQLRequest is a Pydantic model that represents the request body for generating SQL.
    It includes the command to generate SQL for and the schema context.
    """

    command: str
    schema_context: SchemaContext

class RelevantTables(BaseModel):
    tables: list[str]

class FixSQLRequest(CamelModel):
    command: str
    schema_context: SchemaContext
    sql: str
    error_message: str

class FixSQLResponse(BaseModel):
    sql: str
    error: str | None = None