using DbAgent.Common.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace DbAgent.DbExecutor.Models
{
    public record FixAgentRequest(string Command, SchemaContext SchemaContext, string Sql, string ErrorMessage);
}
