from fastapi import FastAPI, Response, status
import logging
from agents import generate_sql, prune_schema_context, correct_error_sql
from models import GenerateSQLRequest, FixSQLRequest, FixSQLResponse
from validator import (
    check_single_valid_statement,
    check_dml_statement,
    check_where_clause_for_update_delete,
    check_dcl_ddl_statements,
    check_schema_conformance,
    check_non_nullable_columns,
    check_conflict_target,
)

logging.basicConfig(level=logging.INFO)

app = FastAPI()

@app.post("/generate", status_code=status.HTTP_200_OK)
async def generate_text(sql: GenerateSQLRequest, response: Response):
    try:
        modified_schema_context = prune_schema_context(sql.command, sql.schema_context)
        if modified_schema_context is None:
            response.status_code = status.HTTP_422_UNPROCESSABLE_ENTITY
            return {"sql": "", "error": "No relevant tables found in the schema context for the given command.", "schema": ""}
        agent_response = generate_sql(sql.command, modified_schema_context) 
    except Exception as e:
        response.status_code = status.HTTP_502_BAD_GATEWAY
        return {"sql": "", "error": f"An error occurred while generating SQL: {str(e)}", "schema": ""}
    
    if agent_response is None:
        response.status_code = status.HTTP_502_BAD_GATEWAY
        return {"sql": "", "error": "Failed to generate SQL.", "schema": ""}
    elif agent_response.error is not None:
        response.status_code = status.HTTP_422_UNPROCESSABLE_ENTITY
        return {"sql": "", "error": agent_response.error, "schema": ""}
    elif agent_response.sql:
        validator_list = [
            check_single_valid_statement,
            check_dml_statement,
            check_where_clause_for_update_delete,
            check_dcl_ddl_statements,
            check_schema_conformance,
            check_non_nullable_columns,
            check_conflict_target
        ]

        for check_fn in validator_list:
            valid, error_message = check_fn(agent_response.sql, modified_schema_context)
            if not valid:
                response.status_code = status.HTTP_422_UNPROCESSABLE_ENTITY
                return {"sql": agent_response.sql, "error": error_message, "schema": ""}
        return {"sql": agent_response.sql, "error": None, "schema": modified_schema_context}
    else:
        response.status_code = status.HTTP_502_BAD_GATEWAY
        return {"sql": "", "error": "Agent returned no SQL and no error.", "schema": ""}


@app.post("/fix", response_model=FixSQLResponse, status_code=status.HTTP_200_OK)
async def fix_sql(sql: FixSQLRequest, response: Response):
    try:
        agent_response = correct_error_sql(sql.command, sql.schema_context, sql.sql, sql.error_message)
    except Exception as e:
        response.status_code = status.HTTP_502_BAD_GATEWAY
        return {"sql": "", "error": f"An error occurred while generating SQL: {str(e)}"}

    if agent_response is None:
            response.status_code = status.HTTP_502_BAD_GATEWAY
            return {"sql": "", "error": "Failed to generate SQL."}
    elif agent_response.error is not None:
        response.status_code = status.HTTP_422_UNPROCESSABLE_ENTITY
        return {"sql": "", "error": agent_response.error}
    elif agent_response.sql:
        validator_list = [
            check_single_valid_statement,
            check_dml_statement,
            check_where_clause_for_update_delete,
            check_dcl_ddl_statements,
            check_schema_conformance,
            check_non_nullable_columns,
            check_conflict_target
        ]

        for check_fn in validator_list:
            valid, error_message = check_fn(agent_response.sql, sql.schema_context)
            if not valid:
                response.status_code = status.HTTP_422_UNPROCESSABLE_ENTITY
                return {"sql": agent_response.sql, "error": error_message}
        return {"sql": agent_response.sql, "error": None}
    else:
        response.status_code = status.HTTP_502_BAD_GATEWAY
        return {"sql": "", "error": "Agent returned no SQL and no error."}