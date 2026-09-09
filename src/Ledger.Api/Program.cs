using Ledger.Api.ErrorHandling;
using Ledger.Application;
using Ledger.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// One error contract for the whole API. AddProblemDetails supplies the RFC 9457
// writer; the handler decides what each exception means.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// The composition root, and the only place that knows every layer exists.
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

var app = builder.Build();

app.UseExceptionHandler();

// No endpoints yet: the HTTP surface arrives with the transfer phase.
app.Run();
