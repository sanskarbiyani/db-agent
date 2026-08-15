import sqlglot
from sqlglot import exp
from sqlglot.errors import ParseError

from models import SchemaContext

def check_single_valid_statement(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command contains only a single statement.

    Args:
        sql_command (str): The SQL command to check.
    """
    try:
        parsed = sqlglot.parse(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."

    if len(parsed) != 1:
        return False, "Multiple SQL statements detected. Please provide a single statement."

    if parsed[0] is None:
        return False, "Atleast one SQL statement is required."
    return True, None


def check_dml_statement(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command is a DML (Data Manipulation Language) statement.

    Args:
        sql_command (str): The SQL command to check.
    """
    try:
        parsed = sqlglot.parse_one(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."

    if isinstance(parsed, (exp.Insert, exp.Update, exp.Delete, exp.Select)):
        return True, None
    else:
        return False, "The provided SQL statement is not a DML / Select statement."


def check_where_clause_for_update_delete(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command is an UPDATE or DELETE statement and contains a WHERE clause.

    Args:
        sql_command (str): The SQL command to check.
    """
    try:
        parsed = sqlglot.parse_one(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."

    if isinstance(parsed, (exp.Update, exp.Delete)):
        if parsed.args.get("where") is None:
            return False, "UPDATE and DELETE statements must include a WHERE clause."
    return True, None


def check_dcl_ddl_statements(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command is a DCL (Data Control Language) or DDL (Data Definition Language) statement.

    Args:
        sql_command (str): The SQL command to check.
    """
    try:
        parsed = sqlglot.parse_one(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."

    if isinstance(parsed, (exp.Create, exp.Alter, exp.Drop, exp.Grant, exp.Revoke)):
        return False, "DCL and DDL statements are not allowed."
    return True, None


def check_schema_conformance(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command conforms to the provided schema context.

    Args:
        sql_command (str): The SQL command to check.
        schema (SchemaContext): The schema context to validate against.
    """
    try:
        parsed = sqlglot.parse_one(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."

    # Check if the tables and columns used in the SQL command exist in the schema context
    tables_in_schema = {table.name for table in schema.tables}
    columns_in_schema = {col.name for table in schema.tables for col in table.columns}

    for table in parsed.find_all(exp.Table):
        if table.name not in tables_in_schema:
            return False, f"Table '{table.name}' does not exist in the schema."

    for column in parsed.find_all(exp.Column):
        if column.name not in columns_in_schema:
            return False, f"Column '{column.name}' does not exist in the schema."

    return True, None


def check_non_nullable_columns(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command provides values for all non-nullable columns in the schema context.

    Args:
        sql_command (str): The SQL command to check.
        schema (SchemaContext): The schema context to validate against.
    """
    try:
        parsed = sqlglot.parse_one(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."

    if isinstance(parsed, exp.Select) or isinstance(parsed, exp.Delete) or isinstance(parsed, exp.Update):
        return True, None  # SELECT statements do not insert data, so we skip this check

    non_nullable_columns = {
        (table.name, col.name)
        for table in schema.tables
        for col in table.columns
        if not col.is_nullable
    }

    if isinstance(parsed, exp.Insert):
        target = parsed.this

        if isinstance(target, exp.Schema):
            table_name = target.this.name
            columns = target.expressions
        else:
            table_name = target.name
            columns = []

        for col in columns:
            col_name = col.name
            if (table_name, col_name) in non_nullable_columns:
                non_nullable_columns.remove((table_name, col_name))

    if non_nullable_columns:
        missing_cols = ", ".join(f"{table}.{col}" for table, col in non_nullable_columns)
        return False, f"Missing values for non-nullable columns: {missing_cols}"

    return True, None


def check_conflict_target(sql_command: str, schema: SchemaContext) -> tuple[bool, str | None]:
    """
    Checks if the provided SQL command is an INSERT statement with an ON CONFLICT clause and validates the conflict target against the schema context.
    """
    try:
        parsed = sqlglot.parse_one(sql_command, dialect="postgres")
    except ParseError:
        return False, "Invalid SQL syntax."
        
    if not isinstance(parsed, exp.Insert):
        return True, None

    on_conflict = parsed.args.get("conflict")
    if on_conflict is None:
        return True, None  # no ON CONFLICT clause, nothing to check

    table_name = parsed.args["this"].name
    if isinstance(parsed.this, exp.Schema):
        table_name = parsed.this.this.name
    else:
        table_name = parsed.this.name


    # extract the conflict target column(s)
    conflict_columns = []
    for identifier in on_conflict.args.get("conflict_keys", []):
        column_node = identifier.this if isinstance(identifier, exp.Ordered) else identifier
        if isinstance(column_node, exp.Column):
            conflict_columns.append(column_node.name)

    if not conflict_columns:
        return False, "ON CONFLICT clause present but no target column specified"

    unique_columns = {
        c.column_name for c in schema.constraints
        if c.table_name == table_name and c.constraint_type == "UNIQUE"
    }

    for col in conflict_columns:
        if col not in unique_columns:
            return False, f"ON CONFLICT target '{col}' has no unique constraint on table '{table_name}'"

    return True, None