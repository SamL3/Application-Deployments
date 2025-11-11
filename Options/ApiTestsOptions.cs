using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace DevApp.Options;

public sealed class ApiTestsOptions
{
    // Bind a collection of tests from appconfig.json:ApiTests
    public List<ApiTestItem> Items { get; init; } = new();
}

public sealed class ApiTestItem
{
    [Required] public string Id { get; init; } = "";
    [Required] public string Description { get; init; } = "";
    [Required] public string ScriptPath { get; init; } = "";
    public List<ApiParam> Parameters { get; init; } = new();
}

public sealed class ApiParam
{
    [Required] public string Name { get; init; } = "";
    public string? Value { get; init; }
}