using System.Text.Json.Serialization;
using Ledger.Api.Endpoints;
using Ledger.Api.ErrorHandling;
using Ledger.Application;
using Ledger.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// One error contract for the whole API. AddProblemDetails supplies the RFC 9457
// writer; the handler decides what each exception means.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Currencies travel as their ISO 4217 alpha code, never as an enum ordinal. A
// number on the wire would break silently the day a member is reordered, and
// means nothing to anyone reading a log.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The composition root, and the only place that knows every layer exists.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

app.MapLedgerEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can host the application.</summary>
public partial class Program;
