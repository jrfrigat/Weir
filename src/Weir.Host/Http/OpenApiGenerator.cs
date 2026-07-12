using Weir.Contracts;

namespace Weir.Host.Http;

/// <summary>
/// Builds an OpenAPI 3.0 document from the endpoint metadata so that data-plane consumers can
/// generate typed clients. The document is assembled as a plain object graph and serialized as JSON.
/// </summary>
public static class OpenApiGenerator
{
    /// <summary>Generates the OpenAPI document for the supplied endpoints.</summary>
    /// <param name="endpoints">The enabled endpoint definitions to describe.</param>
    /// <param name="serverUrl">Absolute base URL of the service (scheme and host), without a trailing slash.</param>
    /// <param name="audience">
    /// Optional description of the filter applied to <paramref name="endpoints"/> (for example
    /// <c>key "checkout-service"</c>), noted in the document title and description so a consumer can tell
    /// a scoped document apart from the full one. Null produces the full-surface document.
    /// </param>
    /// <returns>A serializable object graph representing the OpenAPI document.</returns>
    public static IReadOnlyDictionary<string, object?> Generate(IReadOnlyList<EndpointDefinition> endpoints, string serverUrl, string? audience = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var title = audience is null ? "Weir data API" : $"Weir data API ({audience})";
        var description = audience is null
            ? "Database business logic exposed as HTTP endpoints. Generated from endpoint metadata."
            : $"Database business logic exposed as HTTP endpoints, limited to the surface reachable by {audience}. Generated from endpoint metadata.";

        // Key paths case-insensitively to match the runtime catalog (which resolves routes ignoring
        // case), so case-variant routes merge into one path instead of appearing as separate entries
        // that the server would actually resolve to a single endpoint.
        var paths = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            var path = "/api/" + endpoint.Route.Trim('/');
            if (!paths.TryGetValue(path, out var existing) || existing is not Dictionary<string, object?> operations)
            {
                operations = new Dictionary<string, object?>(StringComparer.Ordinal);
                paths[path] = operations;
            }

            operations[endpoint.HttpMethod.ToLowerInvariant()] = BuildOperation(endpoint);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = title,
                ["version"] = "1.0.0",
                ["description"] = description,
            },
            ["servers"] = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["url"] = serverUrl } },
            ["paths"] = paths,
            ["components"] = BuildComponents(),
            ["security"] = new object[] { new Dictionary<string, object?>(StringComparer.Ordinal) { ["ApiKey"] = Array.Empty<string>() } },
        };
    }

    /// <summary>Builds the operation object for a single endpoint.</summary>
    private static Dictionary<string, object?> BuildOperation(EndpointDefinition endpoint)
    {
        var operation = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = OperationId(endpoint),
            ["summary"] = endpoint.Description ?? $"{endpoint.Schema}.{endpoint.ObjectName}",
            ["tags"] = new object[] { endpoint.ConnectionName },
        };

        if (!string.IsNullOrWhiteSpace(endpoint.Description))
        {
            operation["description"] = endpoint.Description;
        }

        var parameters = new List<object?>();
        var bodyProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var requiredBody = new List<string>();

        foreach (var parameter in endpoint.Parameters)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.ReturnValue)
            {
                continue; // produced by the procedure; never supplied by the caller
            }

            switch (parameter.Source)
            {
                case ParameterSource.Query:
                    parameters.Add(BuildParameter(parameter, "query"));
                    break;
                case ParameterSource.Route:
                    parameters.Add(BuildParameter(parameter, "path"));
                    break;
                case ParameterSource.Header:
                    parameters.Add(BuildParameter(parameter, "header"));
                    break;
                case ParameterSource.Body:
                    var bodySchema = SchemaFor(parameter);
                    if (!parameter.Required)
                    {
                        bodySchema["nullable"] = true;
                    }

                    bodyProperties[parameter.Name] = bodySchema;
                    if (parameter.Required)
                    {
                        requiredBody.Add(parameter.Name);
                    }

                    break;
                case ParameterSource.Claim:
                case ParameterSource.Const:
                default:
                    break; // server-supplied; not part of the client contract
            }
        }

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        if (bodyProperties.Count > 0 && AllowsBody(endpoint.HttpMethod))
        {
            var schema = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["properties"] = bodyProperties,
            };
            if (requiredBody.Count > 0)
            {
                schema["required"] = requiredBody;
            }

            operation["requestBody"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["required"] = requiredBody.Count > 0,
                ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["application/json"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["schema"] = schema },
                },
            };
        }

        operation["responses"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["200"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = "The result envelope.",
                ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["application/json"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["schema"] = Ref("ResponseEnvelope"),
                    },
                },
            },
            ["400"] = ProblemResponse("The request was invalid."),
            ["401"] = ProblemResponse("Authentication is required."),
            ["403"] = ProblemResponse("The API key lacks the required scope."),
            ["429"] = ProblemResponse("The API key exceeded its rate limit."),
        };

        return operation;
    }

    /// <summary>Builds a non-body parameter object (query, path or header).</summary>
    private static Dictionary<string, object?> BuildParameter(EndpointParameter parameter, string location)
    {
        var required = location == "path" || parameter.Required;
        var schema = SchemaFor(parameter);
        if (!required)
        {
            schema["nullable"] = true;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = location == "header" ? parameter.HeaderName ?? parameter.Name : parameter.Name,
            ["in"] = location,
            ["required"] = required,
            ["schema"] = schema,
        };
    }

    /// <summary>Maps an endpoint parameter to its JSON schema fragment.</summary>
    private static Dictionary<string, object?> SchemaFor(EndpointParameter parameter)
    {
        if (parameter.DbType == WeirDbType.Structured)
        {
            var itemProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in parameter.TableColumns ?? [])
            {
                itemProperties[column.Name] = SchemaForType(column.DbType);
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["properties"] = itemProperties,
                },
            };
        }

        return SchemaForType(parameter.DbType);
    }

    /// <summary>Maps a provider-agnostic type to an OpenAPI type and format.</summary>
    private static Dictionary<string, object?> SchemaForType(WeirDbType type) => type switch
    {
        WeirDbType.Boolean => Scalar("boolean"),
        WeirDbType.Byte or WeirDbType.Int16 or WeirDbType.Int32 => Scalar("integer", "int32"),
        WeirDbType.Int64 => Scalar("integer", "int64"),
        WeirDbType.Decimal or WeirDbType.Double or WeirDbType.Single => Scalar("number"),
        WeirDbType.DateTime or WeirDbType.DateTimeOffset => Scalar("string", "date-time"),
        WeirDbType.Date => Scalar("string", "date"),
        WeirDbType.Time => Scalar("string"),
        WeirDbType.Guid => Scalar("string", "uuid"),
        WeirDbType.Binary => Scalar("string", "byte"),
        WeirDbType.Json => new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "object" },
        _ => Scalar("string"),
    };

    /// <summary>Builds a scalar schema with an optional format.</summary>
    private static Dictionary<string, object?> Scalar(string type, string? format = null)
    {
        var schema = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = type };
        if (format is not null)
        {
            schema["format"] = format;
        }

        return schema;
    }

    /// <summary>Builds the reusable components (security scheme and shared schemas).</summary>
    private static Dictionary<string, object?> BuildComponents() => new(StringComparer.Ordinal)
    {
        ["securitySchemes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ApiKey"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "apiKey",
                ["in"] = "header",
                ["name"] = "X-Api-Key",
            },
        },
        ["schemas"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ResponseEnvelope"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["data"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["type"] = "array",
                            ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "object" },
                        },
                    },
                    ["output"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "object" },
                    ["returnValue"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "integer", ["nullable"] = true },
                    ["rowsAffected"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "integer", ["nullable"] = true },
                    ["truncated"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "boolean" },
                    ["messages"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "array",
                        ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "object" },
                    },
                },
            },
            ["ProblemDetails"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = Scalar("string"),
                    ["title"] = Scalar("string"),
                    ["status"] = Scalar("integer", "int32"),
                    ["detail"] = Scalar("string"),
                },
            },
        },
    };

    /// <summary>Builds a problem+json error response referencing the shared schema.</summary>
    private static Dictionary<string, object?> ProblemResponse(string description) => new(StringComparer.Ordinal)
    {
        ["description"] = description,
        ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["application/problem+json"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schema"] = Ref("ProblemDetails"),
            },
        },
    };

    /// <summary>Builds a JSON reference to a component schema.</summary>
    private static Dictionary<string, object?> Ref(string schema) =>
        new(StringComparer.Ordinal) { ["$ref"] = "#/components/schemas/" + schema };

    /// <summary>Derives a stable operationId from the endpoint's method and route.</summary>
    private static string OperationId(EndpointDefinition endpoint)
    {
        var segments = endpoint.Route.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Concat(segments.Select(Capitalize));
        return endpoint.HttpMethod.ToLowerInvariant() + name;
    }

    /// <summary>Upper-cases the first character of a route segment, stripping route-template braces.</summary>
    private static string Capitalize(string segment)
    {
        var cleaned = segment.Trim('{', '}', '*');
        return cleaned.Length == 0 ? string.Empty : char.ToUpperInvariant(cleaned[0]) + cleaned[1..];
    }

    /// <summary>Whether the HTTP method permits a request body.</summary>
    private static bool AllowsBody(string method) =>
        !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);
}
