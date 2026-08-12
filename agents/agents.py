import os
from openai import OpenAI
from dotenv import load_dotenv

from models import SchemaContext, GeneratedSQL, RelevantTables

load_dotenv()

client = OpenAI(api_key=os.getenv("DB_AGENT_KEY"))

def _identify_relevant_tables(command: str, schema_context: SchemaContext) -> list[str]:
    table_info = "\n".join(
        f"- {table.name}: columns [{', '.join(col.name for col in table.columns)}]"
        for table in schema_context.tables
    )
    system_text = f"""
        You are a database schema analyst.
        Given a user's command and a list of tables with their columns, identify
        which table(s) the command refers to or would need to modify.

        Command:
        {command}

        Tables:
        {table_info}

        Rules:
        1. Return only the table names that are directly relevant to the command.
        2. Base your decision on explicit or clearly implied references to a table's
           name or its columns in the command.
        3. Do not include a table just because it is related to another table
           (e.g. via a foreign key) unless the command explicitly involves it.
        4. If no table matches, return an empty list.
    """

    response = client.responses.parse(
            model="gpt-4o-mini",
            input = [
                {"role": "system", "content": system_text}, 
                {"role": "user", "content": command}
            ],
            text_format=RelevantTables
        )
    result = response.output_parsed
    return result.tables if result else []

def prune_schema_context(command: str, schema_context: SchemaContext) -> SchemaContext | None:
    relevant_tables = _identify_relevant_tables(command, schema_context)
    relevant_table_names = set(relevant_tables)
    pruned_tables = [table for table in schema_context.tables if table.name in relevant_table_names]
    if pruned_tables is None or len(pruned_tables) == 0:
        return None
    
    pruned_constraints = [constraint for constraint in schema_context.constraints if constraint.table_name in relevant_table_names]
    return SchemaContext(tables=pruned_tables, constraints=pruned_constraints)


def generate_sql(command: str, schema_context: SchemaContext) -> GeneratedSQL | None:
    """
    Generates SQL statements based on the provided command and schema context.

    Args:
        command (str): The command or query for which SQL needs to be generated.
        schema_context (SchemaContext): The schema the generated SQL must conform to.
    """

    system_text = f"""
        You are a professional SQL Developer. 
        Your task will be to generate sql for the user prompt against the following schema and constraints present.
        Schema: {schema_context.model_dump_json(indent=2)}


        Rules:
        1. Generate a single SQL statement for the user prompt.
        2. Do not include any explanation or comments in the SQL statement.
        3. Use only the tables, columns and constaints provided in the schema context.
        4. If any error is encountered while generating the SQL, return the error message in the 'error' field of the response.
        5. If the user's request requires modifying the database schema in any way — creating, altering, or dropping tables, columns, indexes, constraints, or other schema objects — do not generate SQL. Set 'error' accordingly and leave 'sql' empty.
        6. If the user specifies only some fields to change on an existing-sounding entity, generate a targeted UPDATE statement with a WHERE clause, modifying only the mentioned columns.
        7. If the user provides a full set of fields for a new-sounding entity, generate an INSERT ... ON CONFLICT (<unique key column>) DO UPDATE statement covering the provided columns, to handle the case where the row already exists. If no unique constraint exists on a relevant column, generate a plain INSERT instead.
        8. Only include columns in INSERT or UPDATE statements that the user explicitly provided values for. Do not invent or assume values for unmentioned columns.
        9. Any UPDATE or DELETE statement must include a WHERE clause. Never generate an UPDATE or DELETE without one.
    """
    response = client.responses.parse(
        model="gpt-4o-mini",
        input = [
            {"role": "system", "content": system_text}, 
            {"role": "user", "content": command}
        ],
        text_format=GeneratedSQL
    )

    return response.output_parsed